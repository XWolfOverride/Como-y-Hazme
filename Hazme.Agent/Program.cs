using System.Text;
using Como;
using Hazme.Peer;

const string Usage = """
    hazme-agent: ejecutor remoto TEMPORAL para el hazme de otra máquina.

    Uso:
      hazme-agent [opciones]

    Muestra un token de un solo uso y espera UNA conexión de `hazme -peer id:host:puerto`. Ejecuta los
    comandos que reciba con los permisos de quien lo arranca y los muestra en esta terminal. No tiene IA,
    ni claves, ni configuración, y no escribe nada en disco.

    Opciones:
      -port N                     Puerto (por defecto 7700)
      -listen IP                  Dirección donde escuchar (por defecto 127.0.0.1: úsalo con un túnel ssh)
      -wait MIN                   Se cierra si nadie se conecta en estos minutos (por defecto 10)
      -idle MIN                   Se cierra tras estos minutos sin órdenes (por defecto 30)
      -ttl MIN                    Duración máxima total, aunque esté activo (por defecto 120)
      -no-color                   Sin colores

    Seguridad: una sola conexión; 3 tokens incorrectos y se cierra; Ctrl-C lo cierra (o interrumpe el comando en curso).
    """;

Console.OutputEncoding = new UTF8Encoding(false);

if (args.Any(a => a is "-h" or "--help" or "-help"))
{
    Console.WriteLine(Usage);
    return 0;
}

ConsoleUi.NoColor = args.Contains("-no-color");
return await AgentServer.RunAsync(args.Where(a => a != "-no-color").ToArray());
