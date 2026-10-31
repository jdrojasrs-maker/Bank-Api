# CLAUDE.md

Guía para trabajar en este repositorio. El flujo completo y el diagrama Mermaid están en [README.md](README.md).

## Visión general

Microservicios .NET 10 (`net10.0`, nullable + implicit usings) con arquitectura dirigida por eventos sobre **Azure Service Bus**. Saga coreografiada para una transferencia:
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

Toda la configuración sensible llega por user-secrets / variables de entorno (claves en MAYÚSCULAS: `SERVICEBUSCONSTR`, `SERVICEBUSTOPIC`, `*SQLDBCONSTR`, `NOTIFICATIONDB*`, `SENDGRID*`). **Nunca** poner secretos en `appsettings*.json` ni en commits.

## Estructura por servicio (respetar)

- `Domain/` — `Constants` (nombres de eventos y estados), `Entities`, `Events` (`ProcessEvents : INotification`). Sin dependencias externas.
- `Application/` — interfaces (`IProcessService`, `IDatabaseService`, `IServiceBusSenderService`, ...), `Handlers`, `Models`. Lógica de negocio en `ProcessService`.
- `External/` — adaptadores: `ServiceBusRecive` (BackgroundService), `ServiceBusSender`, `SendGridEmail`.
- `Persistence/` — `DatabaseService` (EF Core `DbContext` o `CosmosClient`).
- `Program.cs` — solo registro de DI.

Patrón de consumo: `ServiceBusReciveService` → `IMediator.Publish(ProcessEvents)` → `ProcessHandler` (crea scope) → `ProcessService.Execute`. Los `ProcessService` son *scoped*, los senders *singleton*.

## Convenciones de eventos

- Un solo tópico (`SERVICEBUSTOPIC`); el nombre del evento va en `ServiceBusMessage.Subject` y coincide con el nombre de la suscripción (kebab-case, p. ej. `transfer-initiated`).
- Los nombres viven en `Domain/Constants` de cada servicio (`ReceivedSubscriptionsConstants` / `SendSubscriptionConstants`). **Los strings deben coincidir entre servicios**; al añadir o renombrar un evento, actualizar emisor, receptor, la suscripción en Azure y la tabla del README.
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
