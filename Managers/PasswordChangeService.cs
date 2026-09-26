using SecureMaid.Helpers;

namespace SecureMaid.Managers;

/// <summary>
/// Centraliza el cambio de contrasena maestra, sea porque el usuario la
/// cambia desde Ajustes (conoce la anterior) o porque la recupero respondiendo
/// su pregunta de seguridad (la "anterior" se obtuvo de ahi). En ambos casos
/// hay que: verificar la contrasena anterior, volver a cifrar la caja fuerte,
/// el indice de ocultos y el de camuflados con la nueva, guardar el nuevo hash, y si hay
/// pregunta de seguridad configurada, re-cifrar tambien el "recovery blob"
/// para que la recuperacion siga funcionando con la contrasena nueva.
/// </summary>
public static class PasswordChangeService
{
    public readonly record struct ChangeResult(bool Success, string? Error);

    /// <param name="oldPassword">Contrasena maestra actual (ya verificada o recuperada).</param>
    /// <param name="newPassword">Contrasena maestra nueva.</param>
    /// <param name="securityAnswer">
    /// Respuesta a la pregunta de seguridad. Si la app tiene una pregunta
    /// configurada, es OBLIGATORIA para poder mantener actualizado el
    /// "recovery blob" (si no, la recuperacion quedaria devolviendo la
    /// contrasena vieja). Si no hay pregunta configurada, se ignora.
    /// </param>
    public static ChangeResult ChangePassword(string oldPassword, string newPassword, string? securityAnswer)
    {
        if (!PasswordManager.VerifyMasterPassword(oldPassword))
            return new ChangeResult(false, "La contraseña actual no es correcta.");

        if (PasswordManager.HasSecurityQuestion())
        {
            if (string.IsNullOrWhiteSpace(securityAnswer) ||
                PasswordManager.TryRecoverPassword(securityAnswer) == null)
            {
                return new ChangeResult(false, "La respuesta a tu pregunta de seguridad no es correcta.");
            }
        }

        var vault = new VaultManager();
        var hide = new HideManager();
        var disguise = new DisguiseRegistry();

        VaultManager.VaultReencryptTransaction? vaultTx = null;
        HideManager.ReencryptTransaction? hideTx = null;
        DisguiseRegistry.ReencryptTransaction? disguiseTx = null;

        try
        {
            // ---- FASE 1: preparar -------------------------------------------------
            // Descifra todo con la contrasena vieja y lo vuelve a cifrar con la nueva
            // en archivos "staged" (".new") junto a los originales. NADA real se toca
            // todavia en esta fase: si algo falla aqui (contrasena vieja que en
            // realidad no abre algun archivo, un archivo cifrado danado, error de
            // E/S), la caja fuerte, los ocultos y los camuflados quedan exactamente
            // como estaban, con la contrasena anterior, tal como promete el mensaje
            // de error.
            try
            {
                vaultTx = vault.PrepareReencrypt(oldPassword, newPassword);
                hideTx = hide.PrepareReencrypt(oldPassword, newPassword);
                disguiseTx = disguise.PrepareReencrypt(oldPassword, newPassword);
            }
            catch (Exception ex)
            {
                return new ChangeResult(false, "No se pudo actualizar la caja fuerte: " + ex.Message);
            }

            // ---- FASE 2: confirmar -------------------------------------------------
            // Ya se comprobo que TODO se puede descifrar con la contrasena actual y
            // volver a cifrar con la nueva; lo unico que queda es reemplazar cada
            // archivo staged por el real (File.Move), una operacion de sistema de
            // archivos, no de criptografia, rapida y con muy baja probabilidad de
            // fallar. Solo despues de que los tres subsistemas confirmen sin errores
            // se actualiza el hash de la contrasena maestra: asi nunca queda un hash
            // que ya no corresponde a lo que hay cifrado en disco.
            try
            {
                vaultTx!.Commit();
                hideTx!.Commit();
                disguiseTx!.Commit();
            }
            catch (Exception ex)
            {
                // Muy improbable (fase 1 ya valido que todo se podia leer/escribir),
                // pero si pasa a mitad de camino, algunos archivos ya quedaron con la
                // contrasena nueva. Se avisa explicitamente en vez de reportar exito
                // o fallo silencioso; NO se toca el hash maestro para no dejar la app
                // en un estado en el que ni la contrasena vieja ni la nueva abren todo.
                return new ChangeResult(false,
                    "El cambio de contraseña se interrumpió a mitad de camino (" + ex.Message + "). " +
                    "Vuelve a intentar el cambio de contraseña con la contraseña actual antes de seguir usando la app.");
            }
        }
        finally
        {
            // Si algo se abortó antes de llegar a Commit(), esto limpia los archivos
            // staged que hayan quedado; si ya se hizo Commit(), Dispose() no hace nada.
            vaultTx?.Dispose();
            hideTx?.Dispose();
            disguiseTx?.Dispose();
        }

        PasswordManager.SetMasterPassword(newPassword);

        if (PasswordManager.HasSecurityQuestion() && !string.IsNullOrWhiteSpace(securityAnswer))
            PasswordManager.UpdateRecoveryBlob(newPassword, securityAnswer);

        Session.CurrentPassword = newPassword;
        return new ChangeResult(true, null);
    }
}
