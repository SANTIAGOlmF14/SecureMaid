# SecureMaid — App de privacidad de archivos para Windows

App de escritorio en **C# + WPF (.NET 8)** para ocultar, cifrar y camuflar
archivos y carpetas en tu propio PC.

## Instalación (usuarios finales)

1. Descargá el instalador desde la última versión publicada en GitHub Releases:
   [`SecureMaid-win-Setup.exe`](https://github.com/SANTIAGOImF14/SecureMaid/releases/latest)
2. Ejecutalo. Se instala como cualquier programa de Windows y deja un acceso
   directo en el Escritorio y en el menú de inicio.
3. No hace falta tener instalado .NET por separado: el instalador incluye
   todo lo necesario para correr (self-contained).

### Actualizaciones automáticas

SecureMaid revisa en segundo plano, cada vez que se abre, si hay una versión
más nueva publicada en GitHub Releases (usando [Velopack](https://velopack.io)).
Si la hay, te pregunta si querés actualizar; si aceptás, la descarga, la
aplica y reabre la app sola. No requiere ninguna acción manual del usuario
más allá de aceptar el diálogo.

## Que incluye

| Función | Cómo funciona | Archivo clave |
|---|---|---|
| **Caja fuerte** | Cifra archivos con AES-256-GCM, los guarda dentro de la app, con **carpetas** para organizarlos y **vista previa** de imagen/video | `Managers/VaultManager.cs`, `Helpers/CryptoHelper.cs`, `Views/ImagePreviewWindow.xaml`, `Views/VideoPreviewWindow.xaml` |
| **Ocultar** | Mueve la carpeta/archivo a un almacén controlado por la app + atributos Oculto/Sistema → deja de existir en la ruta original, por eso no aparece en galerías de fotos/video, búsquedas ni listados | `Managers/HideManager.cs` |
| **Limpieza de Recientes** | Borra accesos `.lnk` de la carpeta Recientes y los Jump Lists, para que no aparezca en "Inicio" del Explorador | `Managers/RecentCleaner.cs` |
| **Camuflar carpeta / archivo** | Renombra + cambia el ícono con `desktop.ini`, usando íconos nativos de Windows (no necesitas archivos .ico externos). Los archivos solo se renombran. **Opcional: protección con contraseña** (cifra el contenido) y **Descamuflar** para dejarlo todo como estaba | `Managers/DisguiseManager.cs`, `Managers/DisguiseRegistry.cs`, `Views/PasswordPromptDialog.xaml` |
| **Camuflar la app** | Accesos directos con nombre/ícono personalizados + modo señuelo (calculadora funcional) | `Managers/AppDisguiseManager.cs`, `Views/DecoyCalculatorView.xaml` |
| **Logo/identidad de la app** | Selector de logo (de la carpeta `Logos`) como icono de ventana/barra de tareas | `Managers/LogoManager.cs` |
| **Auto-actualización** | Revisa, descarga y aplica nuevas versiones publicadas en GitHub Releases | `App.xaml.cs` (Velopack) |

## Requisitos para compilar

1. **Visual Studio 2022** (Community es gratis) con la carga de trabajo *".NET desktop development"*, **o** el **.NET 8 SDK** + VS Code con la extensión de C#.
2. Windows 10/11 (WPF solo corre en Windows).

## Cómo abrir y correr

**Con Visual Studio:**
1. Abre `SecureMaid.csproj` (Archivo → Abrir → Proyecto/Solución).
2. Presiona `F5` para compilar y correr.

**Con VS Code / línea de comandos:**
```bash
cd SecureMaid
dotnet restore
dotnet run
```

La app siempre abre **maximizada / pantalla completa**.

## Primer uso

1. Al abrir por primera vez te pedirá crear una **contraseña maestra** (con un botón 👁 para mostrarla mientras la escribes) y una **pregunta de seguridad** con su respuesta, para poder recuperar el acceso si la olvidas.
2. La sesión queda iniciada mientras la app esté abierta: solo se cierra si tú le das a "Bloquear" o si cierras la app por completo. No vuelve a pedir la contraseña "por sorpresa" en medio del uso.
3. Desde el panel lateral puedes:
   - Agregar archivos a la **Caja fuerte** (los cifra y borra el original), organizarlos en **carpetas** (botón "+ Nueva carpeta"), y ver **imágenes/videos** con doble clic.
   - Seleccionar **varios** archivos a la vez (Ctrl/Shift + clic) para extraer o eliminar en lote.
   - **Ocultar** carpetas/archivos completos.
   - **Camuflar** una carpeta o archivo con otro nombre (e ícono, en carpetas), con contraseña opcional, y **descamuflarlo** desde la lista "Elementos camuflados".
   - Limpiar **Recientes** manualmente si lo necesitas.
   - En **Ajustes**: cambiar el nombre visible de la app, elegir el **logo** (icono de ventana/barra de tareas), activar el **modo señuelo**, crear un acceso directo camuflado en el escritorio, y **cambiar la contraseña maestra** (pide la actual + la nueva, y si tienes pregunta de seguridad, también la respuesta para mantenerla sincronizada). Al cambiarla, toda la caja fuerte se vuelve a cifrar automáticamente con la nueva contraseña.
   - En la pantalla de **inicio de sesión**, si olvidaste la contraseña, usa "¿Olvidaste tu contraseña?" para responder tu pregunta de seguridad y definir una nueva.

## Camuflaje con contraseña y descamuflar

En **Camuflar** puedes activar "Proteger con contraseña (opcional)". Si lo activas, la app te pregunta
qué contraseña usar:

- **Contraseña general de la app**: usa tu contraseña maestra. Si algún día la cambias en Ajustes, la protección
  se actualiza sola.
- **Contraseña nueva e independiente**: una contraseña solo para ese elemento (mínimo 6 caracteres). No se
  guarda en ningún lado ni se puede recuperar: si la olvidas, ese contenido no se puede restaurar.

Si no activas la protección, el elemento queda **solo camuflado** (nombre/ícono) y su contenido no se toca.

**Cómo protege** (`Managers/DisguiseRegistry.cs`): cada elemento protegido recibe una clave aleatoria de 256 bits
con la que se cifra su contenido (AES-256-GCM). Esa clave se guarda "envuelta" con la contraseña elegida en un
índice cifrado (`disguise_index.dat`, en `%AppData%\SecureMaid`). En carpetas, cada archivo de dentro pasa a
`nombre.ext.smx` (se conservan subcarpetas y nombres); en archivos, el contenido se cifra al guardarse con el
nuevo nombre. Sin la contraseña no hay forma de abrirlos, ni desde el Explorador ni con otros programas.

**Descamuflar**: en la tarjeta "Elementos camuflados", selecciona el elemento y pulsa *Descamuflar seleccionado*.
Recupera el nombre original, quita el ícono y, si estaba protegido, descifra el contenido (pide la contraseña
solo si es una contraseña propia; con la general se usa la de tu sesión). Para carpetas camufladas *antes* de
que existiera el registro está *Descamuflar carpeta anterior...* (te pide el nombre que quieres darle).

Detalles a tener en cuenta:

- Cifrar/descifrar es en dos fases: si un archivo está en uso, la contraseña es incorrecta o algún archivo
  cifrado está dañado, **no se cambia nada** (no quedan carpetas a medias).
- No muevas ni renombres un elemento camuflado por fuera de la app: el registro lo busca en su ruta. Si pasa,
  al descamuflar la app te lo avisa y puedes devolverlo a su lugar o quitarlo de la lista.
- Igual que la Caja fuerte, cada archivo se procesa en memoria, así que con archivos muy grandes (varios GB)
  puede tardar o consumir mucha RAM.

## Modo señuelo (calculadora)

Si activas "Modo señuelo" en Ajustes, la próxima vez que abras la app se
mostrará una calculadora **totalmente funcional**. Para revelar el panel
real: escribe tu contraseña maestra con el teclado (no hace falta que se vea
en la pantalla de la calculadora) y presiona **Enter**.

## Cómo funciona la recuperación de contraseña

La clave de cifrado de cada archivo se deriva directo de tu contraseña
maestra (no existe una "clave maestra" intermedia), así que para poder
recuperar el acceso sin perder tus archivos, la app guarda tu contraseña
vigente cifrada con una clave derivada de la respuesta a tu pregunta de
seguridad (`Helpers/PasswordManager.cs`, campo `RecoveryBlob`). Al responder
correctamente la pregunta, la app descifra ese blob, recupera la contraseña
actual, y con eso puede volver a cifrar toda la caja fuerte con la nueva
contraseña que definas (`Managers/PasswordChangeService.cs`).

Esto significa que **cualquiera que sepa la respuesta a tu pregunta de
seguridad puede restablecer tu contraseña**, así que elige una pregunta y
respuesta que no sean obvias ni públicas. Si además de la contraseña olvidas
la respuesta a tu pregunta de seguridad, ahí sí no hay forma de recuperar
los archivos.

## Para desarrolladores: publicar una nueva versión

El instalador y las actualizaciones automáticas se generan con
[Velopack](https://velopack.io) y se distribuyen como GitHub Releases del
propio repo (`https://github.com/SANTIAGOImF14/SecureMaid`, público).

1. Subí `Version`, `FileVersion` y `AssemblyVersion` en `SecureMaid.csproj`
   a la versión nueva (ej. `1.0.1`).
2. Compilá en Release, self-contained, single-file, parado en la carpeta del
   proyecto:
   ```
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
   ```
3. Empaquetá con `vpk` (`dotnet tool update -g vpk` si no lo tenés instalado
   o está desactualizado):
   ```
   vpk pack --packId SecureMaid --packVersion 1.0.1 --packDir .\publish --mainExe SecureMaid.exe --runtime win-x64
   ```
   El resultado queda en `.\Releases` (instalador `.exe`, `.nupkg`, `RELEASES`,
   `releases.win.json`, `assets.win.json`).
4. En GitHub → Releases → *Draft a new release*, tag `v1.0.1`, y subí **todo**
   el contenido de `.\Releases` como assets. Publicá el release.
5. Quienes ya tengan una versión anterior instalada van a recibir el aviso de
   actualización automáticamente la próxima vez que abran la app — no requiere
   ninguna acción manual de tu parte más allá de publicar el release.

No hay firma de código ni sistema de licencias/activación integrado todavía
(se maneja por fuera, en la página de ventas).

## Nota importante

Esta herramienta está pensada para que **tú protejas tus propios archivos en
tu propio equipo** (privacidad personal, no exponer archivos sensibles a
quien use tu PC). El cifrado de la Caja Fuerte y el del camuflaje **con contraseña** son las capas realmente
robustas ante alguien con conocimientos técnicos; el "ocultar" y el
"camuflar" **sin contraseña** son capas para uso cotidiano, no sustituyen al cifrado si el
archivo es muy sensible — para eso, pásalo por la Caja fuerte o camuflalo con contraseña.
