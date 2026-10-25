# DeskWidgets

Dos widgets de escritorio nativos para Windows 10/11 en un solo exe pequeño (un proceso, ~50 MB de RAM, <0,1 % de CPU).

- **Conexión**: SSID, estándar Wi-Fi, banda y canal, señal en % y dBm, velocidad de enlace, descarga/subida en vivo con gráfica de 60 s, latencia a 1.1.1.1, jitter, pérdida de paquetes, latencia al router, dispositivos en la red (pasa el ratón para ver IP, MAC y nombre) y test de velocidad bajo demanda (Cloudflare, unos 16 s).
- **Sistema**: CPU (mismo % que el Administrador de tareas, GHz actuales), RAM, GPU, actividad de disco y MB/s de lectura/escritura, espacio de cada unidad fija, tiempo encendido, procesos y batería.

## Análisis con Claude

El botón **Analizar con Claude** del widget de sistema (o la opción del menú) envía a Claude el estado del PC, los procesos que más CPU y RAM gastan (agrupados por nombre) y el estado de la red con los dispositivos de la LAN (IP, MAC y nombre). Devuelve un diagnóstico y recomendaciones con acciones:

- **Cerrar proceso**: pide confirmación y cierra todas las instancias de esa app en tu sesión. Los procesos del sistema, de seguridad y el propio DeskWidgets nunca se ofrecen.
- **Dispositivo sospechoso**: Windows no puede expulsar dispositivos de la Wi-Fi; el botón abre el panel del router y copia la MAC para bloquearla allí.

Usa tu suscripción de Claude a través de Claude Code en modo headless (`claude -p`), sin API key. Requisito: tener [Claude Code](https://claude.com/claude-code) instalado y haber iniciado sesión una vez con `claude`. Cada análisis tarda entre 20 s y 2 min y consume uso de tu plan.

## Uso

```powershell
.\build.ps1          # compila DeskWidgets.exe con el csc.exe que trae .NET Framework 4.8
.\DeskWidgets.exe
```

- Arrastra un widget para moverlo. La posición se guarda en `%APPDATA%\DeskWidgets\settings.ini`.
- Clic derecho en un widget, o en el icono de la bandeja, para mostrar u ocultar cada widget, dejarlos siempre encima, iniciar con Windows o salir.

## Cómo funciona

Todo se lee con APIs nativas, sin lanzar `netsh`, `typeperf` ni WMI:

| Dato | Fuente |
|---|---|
| Wi-Fi | `wlanapi.dll` (`WlanQueryInterface`) |
| Tráfico | diferencias de `NetworkInterface.GetIPStatistics()` |
| Dispositivos | barrido de ping del /24 cada 3 min y después la tabla ARP (`GetIpNetTable`) |
| CPU, GPU, disco | PDH con rutas de contador en inglés (`PdhAddEnglishCounter`), así funciona en Windows en español |
| RAM, tiempo encendido | `GlobalMemoryStatusEx`, `GetTickCount64` |

## Límites

- Sin temperaturas: Windows solo las expone a procesos con permisos de administrador o a drivers del fabricante.
- En modo escritorio, Win+D oculta los widgets como cualquier otra ventana.
- El escaneo de dispositivos solo cubre el /24 de tu IP. Los dispositivos que no responden a ARP no aparecen.
- El tráfico se mide en el adaptador físico. Una VPN (WARP, WireGuard) no oculta el tráfico, pero los datos del router son los de la red física.
