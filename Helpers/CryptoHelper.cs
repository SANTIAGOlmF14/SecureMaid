using System.IO;
using System.Security.Cryptography;

namespace SecureMaid.Helpers;

/// <summary>
/// Cifrado/descifrado de archivos con AES-256-GCM (autenticado: detecta si el
/// archivo fue alterado). La clave se deriva de la contrasena del usuario con
/// PBKDF2, nunca se guarda la contrasena en texto plano en ningun lado.
///
/// Los archivos NO se cargan enteros en RAM: se procesan en bloques
/// ("chunks") de <see cref="ChunkSize"/> bytes, cada uno con su propio nonce y
/// su propio tag de autenticacion AES-GCM (ver <see cref="BuildChunkNonce"/>).
/// Asi, cifrar/descifrar un video de varios GB usa unos pocos MB de RAM, no
/// varios GB. El nonce de cada chunk codifica su indice y si es el ULTIMO
/// chunk del archivo; el lector reconstruye esa misma secuencia y, si un
/// chunk fue quitado, reordenado o el archivo fue truncado/rellenado, el
/// nonce que reconstruye el lector no coincide con el que uso quien cifro, y
/// AES-GCM rechaza el chunk (devuelve false) en vez de aceptar datos
/// manipulados o incompletos.
///
/// Formato del archivo cifrado (version streaming):
///   [magic "SMV2" 4][salt 16][noncePrefix 8]
///   luego, repetido por cada chunk: [longitud 4 (big-endian)][tag 16][ciphertext...]
/// </summary>
public static class CryptoHelper
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32; // AES-256
    private const int Iterations = 200_000;

    /// <summary>Tamano de bloque para cifrado/descifrado en streaming: 4 MB.
    /// Es el limite superior de cuanta RAM usa procesar un archivo, sin
    /// importar cuan grande sea el archivo en si.</summary>
    private const int ChunkSize = 4 * 1024 * 1024;

    private const int NoncePrefixSize = 8;
    private static readonly byte[] StreamMagic = { (byte)'S', (byte)'M', (byte)'V', 2 };

    /// <param name="iterations">
    /// Numero de iteraciones de PBKDF2. Por defecto <see cref="Iterations"/>
    /// (pensado para no notarse al abrir la caja fuerte). Para derivar claves
    /// a partir de secretos de POCA entropia -- como la respuesta a la
    /// pregunta de seguridad, que alguien con el archivo de configuracion
    /// podria intentar adivinar offline sin limite de intentos -- conviene
    /// pasar un numero mucho mas alto (ver <c>PasswordManager</c>), ya que esa
    /// derivacion solo ocurre una vez por intento de recuperacion y puede
    /// permitirse ser lenta a proposito.
    /// </param>
    public static byte[] DeriveKey(string password, byte[] salt, int iterations = Iterations)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }

    public static void EncryptFile(string inputPath, string outputPath, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = DeriveKey(password, salt);
        try
        {
            EncryptFileStreaming(inputPath, outputPath, key, salt);
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
        }
    }

    private static void EncryptFileStreaming(string inputPath, string outputPath, byte[] key, byte[] salt)
    {
        byte[] noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Se escribe primero a un archivo ".partial" y solo se renombra al
        // destino final si TODO el archivo se cifro sin errores: asi un
        // fallo a mitad de camino (disco lleno, origen en uso, etc.) nunca
        // deja un archivo de salida truncado/invalido en su ruta definitiva.
        string partial = outputPath + ".partial";
        try
        {
            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write))
            using (var aes = new AesGcm(key, TagSize))
            {
                output.Write(StreamMagic);
                output.Write(salt);
                output.Write(noncePrefix);

                byte[] buffer = new byte[ChunkSize];
                byte[] ciphertext = new byte[ChunkSize];
                byte[] tag = new byte[TagSize];
                uint counter = 0;
                long remaining = input.Length;

                while (true)
                {
                    int read = ReadFull(input, buffer, ChunkSize);
                    remaining -= read;
                    bool isLast = remaining <= 0;
                    byte[] nonce = BuildChunkNonce(noncePrefix, counter, isLast);

                    aes.Encrypt(nonce, buffer.AsSpan(0, read), ciphertext.AsSpan(0, read), tag);

                    WriteInt32BE(output, read);
                    output.Write(tag);
                    output.Write(ciphertext, 0, read);

                    counter++;
                    if (isLast) break;
                }
            }
            File.Move(partial, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Devuelve false si la contrasena es incorrecta o el archivo fue alterado
    /// (truncado, con chunks reordenados/quitados, o con datos anadidos).</summary>
    public static bool DecryptFile(string inputPath, string outputPath, string password)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string partial = outputPath + ".partial";
        bool ok;
        try
        {
            ok = DecryptFileStreaming(inputPath, partial, password);
        }
        catch
        {
            // Los datos parciales de un fallo a mitad de camino son texto en
            // claro: se sobreescriben antes de borrarlos, no un Delete simple.
            SecureDelete(partial);
            throw;
        }

        if (!ok)
        {
            SecureDelete(partial);
            return false;
        }

        File.Move(partial, outputPath, overwrite: true);
        return true;
    }

    private static bool DecryptFileStreaming(string inputPath, string outputPath, string password)
    {
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        byte[] magic = new byte[StreamMagic.Length];
        if (ReadFull(input, magic, magic.Length) != magic.Length || !magic.AsSpan().SequenceEqual(StreamMagic))
            return false; // no es un archivo de SecureMaid (o es de una version incompatible)

        byte[] salt = new byte[SaltSize];
        byte[] noncePrefix = new byte[NoncePrefixSize];
        if (ReadFull(input, salt, SaltSize) != SaltSize) return false;
        if (ReadFull(input, noncePrefix, NoncePrefixSize) != NoncePrefixSize) return false;

        byte[] key = DeriveKey(password, salt);
        try
        {
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var aes = new AesGcm(key, TagSize);

            byte[] lenBuf = new byte[4];
            byte[] tag = new byte[TagSize];
            byte[] cipherBuf = new byte[ChunkSize];
            byte[] plainBuf = new byte[ChunkSize];
            uint counter = 0;
            bool sawLastChunk = false;

            while (true)
            {
                int lenRead = ReadFull(input, lenBuf, 4);
                if (lenRead == 0) break; // fin limpio del archivo
                if (lenRead != 4) return false; // corte a mitad de un encabezado de chunk

                int chunkLen = ReadInt32BE(lenBuf);
                if (chunkLen < 0 || chunkLen > ChunkSize) return false;
                if (ReadFull(input, tag, TagSize) != TagSize) return false;
                if (ReadFull(input, cipherBuf, chunkLen) != chunkLen) return false;

                // El escritor decidio "es el ultimo chunk" cuando ya no quedaba
                // nada mas del archivo original por leer; el lector llega a la
                // misma conclusion cuando ya no queda nada mas por leer del
                // archivo cifrado. Si alguien trunco el archivo justo despues
                // de un chunk que NO era el ultimo, aqui se marcaria isLast=true
                // para ese chunk, el nonce no coincidiria con el usado al
                // cifrar, y el Decrypt de abajo fallaria: eso es lo que detecta
                // la manipulacion/truncamiento.
                bool isLast = input.Position >= input.Length;
                byte[] nonce = BuildChunkNonce(noncePrefix, counter, isLast);

                try
                {
                    aes.Decrypt(nonce, cipherBuf.AsSpan(0, chunkLen), tag, plainBuf.AsSpan(0, chunkLen));
                }
                catch (CryptographicException)
                {
                    return false; // contrasena incorrecta, o chunk danado/manipulado/fuera de orden
                }

                output.Write(plainBuf, 0, chunkLen);
                counter++;
                if (isLast) { sawLastChunk = true; break; }
            }

            return sawLastChunk;
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
        }
    }

    /// <summary>Descifra un archivo directo a memoria (sin escribir el contenido en
    /// claro a disco de forma persistente). Pensado para vistas previas de
    /// imagenes: a diferencia de <see cref="DecryptFile"/>, el resultado
    /// completo termina en un arreglo en memoria, asi que no es apropiado
    /// para videos grandes (para esos, usar DecryptFile hacia un archivo
    /// temporal, que es lo que hacen las vistas previas de video).
    /// Devuelve null si la contrasena es incorrecta o el archivo esta danado.</summary>
    public static byte[]? DecryptToBytes(string inputPath, string password)
    {
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        byte[] magic = new byte[StreamMagic.Length];
        if (ReadFull(input, magic, magic.Length) != magic.Length || !magic.AsSpan().SequenceEqual(StreamMagic))
            return null;

        byte[] salt = new byte[SaltSize];
        byte[] noncePrefix = new byte[NoncePrefixSize];
        if (ReadFull(input, salt, SaltSize) != SaltSize) return null;
        if (ReadFull(input, noncePrefix, NoncePrefixSize) != NoncePrefixSize) return null;

        byte[] key = DeriveKey(password, salt);
        try
        {
            using var result = new MemoryStream();
            using var aes = new AesGcm(key, TagSize);

            byte[] lenBuf = new byte[4];
            byte[] tag = new byte[TagSize];
            byte[] cipherBuf = new byte[ChunkSize];
            byte[] plainBuf = new byte[ChunkSize];
            uint counter = 0;
            bool sawLastChunk = false;

            while (true)
            {
                int lenRead = ReadFull(input, lenBuf, 4);
                if (lenRead == 0) break;
                if (lenRead != 4) return null;

                int chunkLen = ReadInt32BE(lenBuf);
                if (chunkLen < 0 || chunkLen > ChunkSize) return null;
                if (ReadFull(input, tag, TagSize) != TagSize) return null;
                if (ReadFull(input, cipherBuf, chunkLen) != chunkLen) return null;

                bool isLast = input.Position >= input.Length;
                byte[] nonce = BuildChunkNonce(noncePrefix, counter, isLast);

                try
                {
                    aes.Decrypt(nonce, cipherBuf.AsSpan(0, chunkLen), tag, plainBuf.AsSpan(0, chunkLen));
                }
                catch (CryptographicException)
                {
                    return null;
                }

                result.Write(plainBuf, 0, chunkLen);
                counter++;
                if (isLast) { sawLastChunk = true; break; }
            }

            return sawLastChunk ? result.ToArray() : null;
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
        }
    }

    private static byte[] BuildChunkNonce(byte[] noncePrefix, uint counter, bool isLast)
    {
        byte[] nonce = new byte[NonceSize];
        Buffer.BlockCopy(noncePrefix, 0, nonce, 0, NoncePrefixSize);
        // El bit mas alto marca "es el ultimo chunk"; deja ~2 mil millones de
        // chunks de indice (con ChunkSize=4MB, archivos de miles de TB), de
        // sobra para cualquier archivo real.
        uint counterField = isLast ? (counter | 0x8000_0000u) : counter;
        nonce[NoncePrefixSize] = (byte)(counterField >> 24);
        nonce[NoncePrefixSize + 1] = (byte)(counterField >> 16);
        nonce[NoncePrefixSize + 2] = (byte)(counterField >> 8);
        nonce[NoncePrefixSize + 3] = (byte)counterField;
        return nonce;
    }

    private static void WriteInt32BE(Stream s, int value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    private static int ReadInt32BE(byte[] buf) =>
        (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];

    /// <summary>Lee hasta <paramref name="count"/> bytes, repitiendo la lectura
    /// hasta llenar el buffer o llegar al final del stream (un solo Read no
    /// garantiza traer todo lo pedido, sobre todo en unidades de red).</summary>
    private static int ReadFull(Stream s, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Cifra un arreglo de bytes en memoria con una contrasena/clave arbitraria
    /// (mismo formato que EncryptFile: salt+nonce+tag+ciphertext, todo en un solo
    /// arreglo). Se usa para la pregunta de seguridad: guarda la contrasena maestra
    /// cifrada con una clave derivada de la respuesta, para poder recuperarla.</summary>
    public static byte[] EncryptBytes(byte[] plaintext, string password, int iterations = Iterations)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] key = DeriveKey(password, salt, iterations);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        Array.Clear(key, 0, key.Length);

        byte[] result = new byte[SaltSize + NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(salt, 0, result, 0, SaltSize);
        Buffer.BlockCopy(nonce, 0, result, SaltSize, NonceSize);
        Buffer.BlockCopy(tag, 0, result, SaltSize + NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, SaltSize + NonceSize + TagSize, ciphertext.Length);
        return result;
    }

    /// <summary>Descifra lo generado por EncryptBytes. Devuelve null si la
    /// contrasena/clave es incorrecta o los datos estan corruptos.</summary>
    public static byte[]? DecryptBytes(byte[] blob, string password, int iterations = Iterations)
    {
        if (blob.Length < SaltSize + NonceSize + TagSize) return null;

        byte[] salt = blob[..SaltSize];
        byte[] nonce = blob[SaltSize..(SaltSize + NonceSize)];
        byte[] tag = blob[(SaltSize + NonceSize)..(SaltSize + NonceSize + TagSize)];
        byte[] ciphertext = blob[(SaltSize + NonceSize + TagSize)..];
        byte[] key = DeriveKey(password, salt, iterations);
        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
        }
    }

    /// <summary>Sobreescribe un archivo con datos aleatorios antes de borrarlo.
    ///
    /// IMPORTANTE - que garantiza y que NO garantiza esto:
    /// En un disco mecanico (HDD) tradicional, sobreescribir antes de borrar
    /// reduce mucho las chances de recuperar el archivo con herramientas
    /// comunes de "undelete". En un SSD (o cualquier almacenamiento con
    /// wear-leveling, como USBs/tarjetas modernas) esto NO es una garantia:
    /// el controlador puede redirigir la escritura a celdas fisicas distintas
    /// de donde estaba el archivo original (wear leveling), mantener copias
    /// por TRIM/garbage collection diferido, etc. Osea, esta funcion AYUDA a
    /// dificultar la recuperacion con herramientas comunes, pero no debe
    /// describirse ni entenderse como "destruye el archivo de forma
    /// irrecuperable" en SSD. Para ese nivel de garantia hace falta cifrado
    /// de disco completo (BitLocker) desde el principio, o borrado seguro a
    /// nivel de firmware (ATA Secure Erase) del dispositivo completo.</summary>
    public static void SecureDelete(string path)
    {
        if (!File.Exists(path)) return;
        long length = new FileInfo(path).Length;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            byte[] junk = RandomNumberGenerator.GetBytes((int)Math.Min(length, 1024 * 1024));
            long written = 0;
            while (written < length)
            {
                int chunk = (int)Math.Min(junk.Length, length - written);
                fs.Write(junk, 0, chunk);
                written += chunk;
            }
            fs.Flush(flushToDisk: true);
        }
        File.Delete(path);
    }

    /// <summary>Crea un archivo temporal vacio en una carpeta PRIVADA de la app
    /// (dentro de %AppData%\SecureMaid\tmp), en vez de usar Path.GetTempFileName(),
    /// que crea archivos en la carpeta %TEMP% compartida por todo el sistema.
    /// Se usa para los pocos casos en que un archivo grande necesita pasar por
    /// texto plano en disco de forma transitoria (p. ej. al re-cifrar la caja
    /// fuerte con una contrasena nueva); ese archivo se debe borrar siempre con
    /// <see cref="SecureDelete"/>, nunca con File.Delete.</summary>
    public static string CreatePrivateTempFile()
    {
        string dir = Path.Combine(PasswordManager.AppDataFolder, "tmp");
        Directory.CreateDirectory(dir);
        try { File.SetAttributes(dir, File.GetAttributes(dir) | FileAttributes.Hidden); } catch { /* no critico */ }

        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
        using (File.Create(path)) { }
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* limpieza de mejor esfuerzo */ }
    }

    // ---------------------------------------------------------------------
    // Cifrado con clave directa (sin contrasena/PBKDF2 por archivo).
    // Lo usa el Camuflaje con contrasena: cada elemento camuflado tiene una
    // clave aleatoria de 256 bits (protegida aparte por la contrasena elegida),
    // asi que no hace falta derivar una clave con 200.000 iteraciones por cada
    // archivo de una carpeta. Formato: [magic "SMX"+version 4 bytes][nonce 12][tag 16][ciphertext...]
    // El magic va como dato asociado (AAD): si se altera, el descifrado falla.
    // ---------------------------------------------------------------------

    private static readonly byte[] KeyedMagic = { (byte)'S', (byte)'M', (byte)'X', 1 };

    public static byte[] GenerateRandomKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>Igual que EncryptFile pero con una clave ya lista (sin PBKDF2) y en
    /// streaming (no carga el archivo entero en RAM: mismo motivo que EncryptFile,
    /// util para carpetas camufladas que contengan videos u otros archivos grandes).</summary>
    public static void EncryptFileWithKey(string inputPath, string outputPath, byte[] key)
    {
        byte[] noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string partial = outputPath + ".partial";
        try
        {
            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write))
            using (var aes = new AesGcm(key, TagSize))
            {
                output.Write(KeyedMagic);
                output.Write(noncePrefix);

                byte[] buffer = new byte[ChunkSize];
                byte[] ciphertext = new byte[ChunkSize];
                byte[] tag = new byte[TagSize];
                uint counter = 0;
                long remaining = input.Length;

                while (true)
                {
                    int read = ReadFull(input, buffer, ChunkSize);
                    remaining -= read;
                    bool isLast = remaining <= 0;
                    byte[] nonce = BuildChunkNonce(noncePrefix, counter, isLast);

                    aes.Encrypt(nonce, buffer.AsSpan(0, read), ciphertext.AsSpan(0, read), tag, KeyedMagic);

                    WriteInt32BE(output, read);
                    output.Write(tag);
                    output.Write(ciphertext, 0, read);

                    counter++;
                    if (isLast) break;
                }
            }
            File.Move(partial, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>True si el archivo empieza con la cabecera streaming de EncryptFileWithKey
    /// (formato nuevo) o con la cabecera del formato de un solo bloque anterior.</summary>
    public static bool HasKeyedHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < KeyedMagic.Length) return false;
            byte[] head = new byte[KeyedMagic.Length];
            int read = fs.Read(head, 0, head.Length);
            return read == head.Length && head.AsSpan().SequenceEqual(KeyedMagic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Devuelve false si la clave es incorrecta, el archivo fue alterado
    /// (incluye truncado o con chunks reordenados/quitados) o no tiene el formato
    /// de EncryptFileWithKey.</summary>
    public static bool DecryptFileWithKey(string inputPath, string outputPath, byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string partial = outputPath + ".partial";
        bool ok;
        try
        {
            ok = DecryptFileWithKeyStreaming(inputPath, partial, key);
        }
        catch
        {
            SecureDelete(partial);
            throw;
        }

        if (!ok)
        {
            SecureDelete(partial);
            return false;
        }

        File.Move(partial, outputPath, overwrite: true);
        return true;
    }

    private static bool DecryptFileWithKeyStreaming(string inputPath, string outputPath, byte[] key)
    {
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        byte[] magic = new byte[KeyedMagic.Length];
        if (ReadFull(input, magic, magic.Length) != magic.Length || !magic.AsSpan().SequenceEqual(KeyedMagic))
            return false;

        byte[] noncePrefix = new byte[NoncePrefixSize];
        if (ReadFull(input, noncePrefix, NoncePrefixSize) != NoncePrefixSize) return false;

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var aes = new AesGcm(key, TagSize);

        byte[] lenBuf = new byte[4];
        byte[] tag = new byte[TagSize];
        byte[] cipherBuf = new byte[ChunkSize];
        byte[] plainBuf = new byte[ChunkSize];
        uint counter = 0;
        bool sawLastChunk = false;

        while (true)
        {
            int lenRead = ReadFull(input, lenBuf, 4);
            if (lenRead == 0) break;
            if (lenRead != 4) return false;

            int chunkLen = ReadInt32BE(lenBuf);
            if (chunkLen < 0 || chunkLen > ChunkSize) return false;
            if (ReadFull(input, tag, TagSize) != TagSize) return false;
            if (ReadFull(input, cipherBuf, chunkLen) != chunkLen) return false;

            bool isLast = input.Position >= input.Length;
            byte[] nonce = BuildChunkNonce(noncePrefix, counter, isLast);

            try
            {
                aes.Decrypt(nonce, cipherBuf.AsSpan(0, chunkLen), tag, plainBuf.AsSpan(0, chunkLen), KeyedMagic);
            }
            catch (CryptographicException)
            {
                return false;
            }

            output.Write(plainBuf, 0, chunkLen);
            counter++;
            if (isLast) { sawLastChunk = true; break; }
        }

        return sawLastChunk;
    }
}
