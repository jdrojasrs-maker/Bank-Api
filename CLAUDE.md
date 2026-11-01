# CLAUDE.md

Guía para trabajar en este repositorio. El flujo completo y el diagrama Mermaid están en [README.md](README.md).

## Visión general

Microservicios .NET 10 (`net10.0`, nullable + implicit usings) con arquitectura dirigida por eventos sobre **Azure Service Bus** (en local, su emulador; ver "Entorno local"). Saga coreografiada para una transferencia:
`Gateway → Transaction → Balance → Transaction → Transfer → Transaction → Notification (+ Balance)`.

Solución: `Bank.slnx`. Servicios en `src/Bank.<Servicio>/Bank.<Servicio>.Api/` (Gateway usa `.API`).
`src/Bank.Notification/Bank.Notification` es una plantilla sin uso fuera de la solución: no tocarla ni basarse en ella.

## Comandos

```bash
dotnet build Bank.slnx
dotnet run --project src/Bank.<Servicio>/Bank.<Servicio>.Api
dotnet user-secrets set "<CLAVE>" "<valor>" --project <proyecto>
```

Puertos de desarrollo: ver cada `Properties/launchSettings.json`. No hay proyecto de pruebas aún.

## Configuración

Toda la configuración sensible llega por user-secrets / variables de entorno (claves en MAYÚSCULAS: `SERVICEBUSCONSTR`, `SERVICEBUSTOPIC`, `*SQLDBCONSTR`, `NOTIFICATIONDB*`, `SENDGRID*`). **Nunca** poner secretos en `appsettings*.json` ni en commits. No leer ni mostrar valores de secretos o del `.env`; al verificarlos, ocultar contraseñas.

Estado actual de los user-secrets (los recursos de Azure originales ya no existen):

| Clave | Valor actual |
|---|---|
| `SERVICEBUSCONSTR` (los 5 servicios) | Emulador local: `Endpoint=sb://localhost;...;UseDevelopmentEmulator=true;` |
| `SERVICEBUSTOPIC` (los 5) | `bank-transfer` |
| `TRANSACTIONSQLDBCONSTR` / `BALANCESQLDBCONSTR` / `TRANSFERSQLDBCONSTR` | `Server=127.0.0.1,1433;Database=BankTransaction` / `BankBalance` / `BankTransfer`; usuario `sa`, `TrustServerCertificate=True` |
| `NOTIFICATIONDB*`, `SENDGRID*` | **Pendientes**: Cosmos DB y SendGrid aún no se trabajan; Notification no funcionará de punta a punta. No intentar arreglarlo sin que se pida. |

Usar `127.0.0.1`, no `localhost`: `localhost` resuelve primero a `::1` (IPv6) y el puerto solo se publica en IPv4, lo que produce timeouts.

## Entorno local (`infra/local/`)

Todo el entorno local vive aquí; los servicios corren en el host con `dotnet run` y usan lo siguiente en contenedores:

| Archivo | Para qué |
|---|---|
| `Config.json` | Entidades del emulador de Service Bus: namespace `sbemulatorns`, topic `bank-transfer` y 11 suscripciones (una por evento), cada una con una regla `CorrelationFilter` cuyo `Label` es el nombre del evento (equivale al `Subject` del mensaje). |
| `docker-compose.yml` | Dos contenedores: `bank-sqlserver` (`mssql/server:2022-latest`, publicado en `127.0.0.1:1433`) y `bank-servicebus-emulator` (puertos `5672` AMQP y `5300` health/admin). El emulador usa SQL Server como almacenamiento interno y espera a que esté `healthy`. |
| `.env.example` | Plantilla de variables. Copiar a `.env` (ignorado por git) y poner `SQL_PASSWORD` (mín. 8 caracteres y 3 de 4 clases: mayúsculas, minúsculas, números, símbolos). **`.env.example` nunca lleva contraseña.** |
| `apply-migrations.ps1` | Aplica las migraciones de EF Core a las tres bases de datos. |
| `smoke-test/` | App de consola que verifica que el enrutamiento por `Subject`/`Label` funciona (`dotnet run`, 4 comprobaciones `[OK]`). |

### Operación

```powershell
cd infra/local
docker compose up -d                  # SQL Server + emulador
.\apply-migrations.ps1                # crear/actualizar esquema (tras cada "down")
curl.exe http://localhost:5300/health # 200 = emulador listo
docker compose restart emulator       # tras editar Config.json
docker compose down                   # apagar (borra todo, ver abajo)
```

Cadena del emulador: `Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;`

### Comportamientos que no son obvios

- **Sin persistencia** (documentado por Microsoft): tras reiniciar el emulador se pierden mensajes y entidades; solo se recrean las del `Config.json`. SQL Server tampoco tiene volumen: `docker compose down` borra las tres bases, así que hay que volver a ejecutar `apply-migrations.ps1`.
- **Si se recrea el contenedor de SQL, reiniciar también el emulador** (`docker compose restart emulator`): su metadata vivía en esa base.
- Los cambios en `Config.json` no se aplican en caliente: requieren reiniciar el emulador. El `Config.json` prevalece sobre entidades creadas con el Administration Client (soportado de forma nativa solo en .NET, con el puerto 5300 en la cadena).
- Límites del emulador: un único namespace (`sbemulatorns`, no renombrable), 50 suscripciones por topic, mensaje de 256 KB, TTL máximo 1 h, 10 conexiones concurrentes, solo AMQP sobre TCP, sin Entra ID.
- Al añadir o renombrar un evento: actualizar constantes en emisor y receptor, `infra/local/Config.json` (suscripción + regla) y la tabla del README.

### Migraciones de EF Core (Transaction, Balance, Transfer)

- Herramienta fijada en `dotnet-tools.json` (`dotnet-ef` 10.0.12); usar `dotnet tool run dotnet-ef ...` desde la raíz, no la global.
- Cada servicio tiene su migración en `Persistence/Migrations`. Para un cambio de modelo:
  ```powershell
  dotnet tool run dotnet-ef migrations add <Nombre> --project src/Bank.<Servicio>/Bank.<Servicio>.Api --output-dir Persistence/Migrations
  .\infra\local\apply-migrations.ps1
  ```
- `Microsoft.EntityFrameworkCore.Design` es dependencia solo de desarrollo (`PrivateAssets=all`).
- Las migraciones se aplican por comando, no al arrancar el servicio.
- Notification usa Cosmos DB, sin migraciones.

## Estructura por servicio (respetar)

- `Domain/` — `Constants` (nombres de eventos y estados), `Entities`, `Events` (`ProcessEvents : INotification`). Sin dependencias externas.
- `Application/` — interfaces (`IProcessService`, `IDatabaseService`, `IServiceBusSenderService`, ...), `Handlers`, `Models`. Lógica de negocio en `ProcessService`.
- `External/` — adaptadores: `ServiceBusRecive` (BackgroundService), `ServiceBusSender`, `SendGridEmail`.
- `Persistence/` — `DatabaseService` (EF Core `DbContext` o `CosmosClient`).
- `Program.cs` — solo registro de DI.

Patrón de consumo: `ServiceBusReciveService` → `IMediator.Publish(ProcessEvents)` → `ProcessHandler` (crea scope) → `ProcessService.Execute`. Los `ProcessService` son *scoped*, los senders *singleton*.

## Convenciones de eventos

- Un solo tópico (`SERVICEBUSTOPIC`); el nombre del evento va en `ServiceBusMessage.Subject` y coincide con el nombre de la suscripción (kebab-case, p. ej. `transfer-initiated`).
- Los nombres viven en `Domain/Constants` de cada servicio (`ReceivedSubscriptionsConstants` / `SendSubscriptionConstants`). **Los strings deben coincidir entre servicios**; al añadir o renombrar un evento, actualizar emisor, receptor, `infra/local/Config.json` (y la suscripción en Azure, si existe) y la tabla del README.
- Payload JSON; el `CorrelationId` creado en el Gateway viaja en todos los eventos y debe ser la clave de idempotencia/consulta en cada BD.
- Estados: `Pending`, `Completed`, `Canceled` (Transfer usa `Complete`).
- Serialización: emisores con `System.Text.Json`, receptores con `Newtonsoft.Json` (case-insensitive). Mantener compatibilidad de campos.

## Principios EDD a respetar al cambiar código

- Servicios desacoplados: nada de referencias de proyecto ni llamadas HTTP entre servicios; solo eventos.
- Handlers **idempotentes** (Service Bus entrega *at-least-once*): comprobar por `CorrelationId` antes de insertar.
- Todo camino de error debe emitir su evento de fallo/compensación (`*-failed`) en vez de perderse.
- Cada servicio es dueño de su BD; no compartir tablas.

## Deuda técnica conocida (ver README)

`ProcessError` vacío, sin dead-letter handling ni Outbox, `Id != 0` como único criterio de fallo, destinatario de email hardcodeado en Notification, senders creados por mensaje sin dispose, Gateway sin auth ni `CorrelationId` en la respuesta, `Transfer/External/SendGridEmail` vacío. Si el cambio toca estas zonas, señálala, pero no la arregles sin que se pida.

## Estilo

Seguir el código existente: namespaces file-scoped, constantes `UPPER_SNAKE_CASE`, sufijos `Service`/`Handler`, un `Execute` por servicio/adaptador. Nota: los nombres `ServiceBusRecive` (sin "e") son los reales; no "corregirlos" parcialmente.
