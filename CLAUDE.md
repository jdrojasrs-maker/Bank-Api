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
dotnet run --project src/Bank.<Servicio>/Bank.<Servicio>.Api --launch-profile http
dotnet user-secrets set "<CLAVE>" "<valor>" --project <proyecto>
```

Siempre correr los servicios en el host con el perfil **`http`**. El perfil "Container (Dockerfile)" de Visual Studio no sirve en local: dentro del contenedor `localhost` no alcanza el emulador ni SQL (síntoma: `Connection refused` en `ServiceBusSenderService`).

Puertos (perfil `http`): Gateway 5204, Transaction 5110, Balance 5287, Transfer 5197, Notification 5267. El endpoint de entrada es `POST http://localhost:5204/api-gateway`.

Pruebas automáticas (un proyecto por servicio: Transfer, Transaction, Balance y Notification). Usan xUnit; Transfer, Transaction y Balance usan un SQL Server real en contenedor (Testcontainers) con las migraciones reales, por lo que **requieren Docker en marcha** (las de Notification no usan base de datos):

```bash
dotnet test tests/Bank.Transfer.Api.Tests
dotnet test tests/Bank.Transaction.Api.Tests
dotnet test tests/Bank.Balance.Api.Tests
dotnet test tests/Bank.Notification.Api.Tests
```

Ningún proyecto de pruebas debe referenciar a más de un servicio. Toda corrección lleva su prueba (ver `.specify/memory/constitution.md`, Principio V).

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

## Integración continua (GitHub Actions)

`.github/workflows/ci.yml` se ejecuta en cada pull request hacia `main` o `develop` y en cada push a esas ramas:

- **Build**: restaura y compila `Bank.slnx` en Release (más una auditoría informativa de paquetes vulnerables que no bloquea).
- **Tests (Transfer)**, **Tests (Transaction)**, **Tests (Balance)** y **Tests (Notification)**: un job por servicio, en paralelo. Los tres primeros usan el Docker del runner para levantar SQL Server con Testcontainers; Notification no usa base de datos. Ninguno requiere secretos.
- Los cinco checks están exigidos en la protección de `main` y `develop` (ajuste de la web de GitHub, no un archivo); sus nombres deben coincidir exactamente con los de los jobs (`Build`, no `build`). No desactivar ni relajar un check para que un cambio pase: se corrige la causa.
- Flujo: rama nueva desde `develop` actualizado (`feat/`, `fix/`, `ci/`) → push → pull request a `develop` con los cinco checks en verde → merge. Nunca commit directo a `main`/`develop`.
- Cada job de pruebas sube su `.trx` como artefacto, y una ejecución nueva sobre el mismo PR cancela la anterior.
- `System.Security.Cryptography.Pkcs` está fijado a 10.0.14 en Transfer, Transaction y Balance solo para corregir la versión vulnerable (9.0.11) que arrastra `Microsoft.Data.SqlClient` 6.1.6; quitar la referencia cuando SqlClient la corrija. El paso de auditoría de la CI debe salir limpio.
- El runner es Linux (distingue mayúsculas): cuidar la capitalización de rutas y de referencias entre proyectos.
- Los workflows no se pueden ejecutar en local; se validan subiendo la rama y mirando la pestaña *Actions*.

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
- Estados: `Pending`, `Completed`, `Canceled` (Transfer usa `Complete`). En Transaction, `Completed` y `Canceled` son **finales**: ningún mensaje duplicado o tardío los cambia.
- Serialización: emisores con `System.Text.Json`, receptores con `Newtonsoft.Json` (case-insensitive). Mantener compatibilidad de campos.

## Idempotencia y mensajes ignorados

- **Transfer**: `TransferRequest` (una fila por `CorrelationId`, índice único) guarda el resultado (`Confirmed`/`Failed`, también los rechazos), la huella del contenido y si ya se emitió. La emisión se toma con un *claim* de `EMISSION_LEASE_SECONDS` (30 s): un duplicado no registra ni emite de nuevo y solo se recupera una emisión pendiente cuando el lease venció.
- **Transaction**: solo `transaction-initiated` crea la fila; las transiciones se aplican únicamente desde `Pending` con una actualización condicional, y se emite **antes** de confirmar la transición (un fallo de envío deja `Pending` y la reentrega reintenta).
- `CorrelationId` es `nvarchar(64)` con índice único en `Transfer`, `TransferRequest` y `Transaction`. Una violación de unicidad (SQL 2601/2627) significa "ya procesado", **no** un fallo: `DatabaseErrors.IsUniqueViolation` la distingue de `IsPermanent`.
- Un mensaje ignorado (duplicado, tardío, sin `CorrelationId`) se completa y se registra con `CorrelationId`, `MessageType` y `Reason` (constantes `IgnoredMessageReasonConstants`).
- Diseño y decisiones: `specs/001-idempotent-transfer-processing/` (`research.md`, `data-model.md`).

## Resiliencia en el consumo (feature 002)

Detalle y decisiones: `specs/002-resilient-message-consumption/` (`contracts/message-handling.md`, `research.md`). El código vive en `Application/Resilience/` de cada servicio y está **duplicado a propósito** en los cuatro (la constitución prohíbe un proyecto compartido).

- Los receptores usan `AutoCompleteMessages = false`; `ResilientMessageProcessor.HandleAsync` decide completar, reprogramar o apartar cada mensaje.
- **Tres clases de error**: *venenoso* (cuerpo vacío o ilegible, sin `CorrelationId`, `CorrelationId` de más de 64 caracteres, campo con tipo incorrecto) → DLQ en la primera entrega con motivo `PoisonMessage`; *permanente de datos* (SQL 515/547/8115/8152) → lo resuelve `ProcessService` (emite `*-failed` y completa); *transitorio* (todo lo demás) → reintento. Violación de unicidad (2601/2627) = "ya procesado".
- **Reintentos**: se programa una copia con `ScheduleMessageAsync` (mismo `Subject`, propiedad `RetryAttempt`) y **después** se completa el original. Hasta 5 reintentos con espera `5 s·3^(n−1)`, tope 300 s, ±20 % (`RetryPolicy`). Un cierre ordenado del servicio no reprograma ni cuenta como fallo.
- **Agotados los reintentos**: se compensa y el mensaje va a la DLQ con motivo `RetriesExhausted` (`Attempts = 6`, último error en el detalle, máx. 1 024 caracteres). Si no se pudo emitir el evento de fallo, el motivo es `CompensationFailed`.
- **Compensación solo del primer paso**: `transaction-initiated` → `transaction-failed`, `balance-initiated` → `balance-failed`, `transfer-initiated` → `transfer-failed`. Antes de emitir, `FailureCompensator` comprueba que su propia BD **no** tenga registro del `CorrelationId` (si lo tiene, no emite: la saga ya avanzó); si la BD no responde, emite. El payload es `{CorrelationId, CustomerId}` y **omite** `CustomerId` si no se pudo leer (un `null` explícito haría que el consumidor lo rechace como venenoso). Los manejadores que solo actualizan estado no compensan: se registran y se apartan.
- `ProcessError` **solo registra** (`ProcessorErrorHandler`): no aparta, completa ni compensa.
- Cada mensaje apartado o reprogramado se registra con `CorrelationId`, `MessageId`, `MessageType`, `Attempt`, `Classification` y `Reason`.
- Mensajes manuales para probar (cuerpo inválido, `RetryAttempt`): `infra/local/duplicate-test` con `--raw`.

## Principios EDD a respetar al cambiar código

- Servicios desacoplados: nada de referencias de proyecto ni llamadas HTTP entre servicios; solo eventos.
- Handlers **idempotentes** (Service Bus entrega *at-least-once*): comprobar por `CorrelationId` antes de insertar.
- Todo camino de error debe emitir su evento de fallo/compensación (`*-failed`) en vez de perderse.
- Cada servicio es dueño de su BD; no compartir tablas.

## Deuda técnica conocida (ver README)

sin Outbox, manejadores de solo-actualización (p. ej. `balance-confirmed`, `transfer-confirmed-balance`) sin evento de fallo al agotar los reintentos (solo se registran y van a la DLQ), sin alerta de longitud de la DLQ ni herramienta de reproceso, Balance y Notification sin idempotencia, destinatario de email hardcodeado en Notification, senders creados por mensaje sin dispose, Gateway sin auth ni `CorrelationId` en la respuesta, `Transfer/External/SendGridEmail` vacío. Si el cambio toca estas zonas, señálala, pero no la arregles sin que se pida.

## Estilo

Seguir el código existente: namespaces file-scoped, constantes `UPPER_SNAKE_CASE`, sufijos `Service`/`Handler`, un `Execute` por servicio/adaptador. Nota: los nombres `ServiceBusRecive` (sin "e") son los reales; no "corregirlos" parcialmente.
