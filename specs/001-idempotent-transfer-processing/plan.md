# Implementation Plan: Procesamiento idempotente de transferencias

**Branch**: `001-idempotent-transfer-processing` (rama no creada; el trabajo continúa en `wb-feat-s301001`) | **Date**: 2026-11-03 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-idempotent-transfer-processing/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Requisito principal: un `transfer-initiated` repetido con el mismo `CorrelationId` debe dejar **una sola transferencia y un solo
resultado**, y una transacción en estado final (`Completed`/`Canceled`) **nunca** debe volver a `Pending` ni disparar eventos por un
mensaje duplicado o tardío.

Enfoque técnico (detalle en [research.md](research.md)):
- **Transfer**: nueva tabla `TransferRequest` con índice único por `CorrelationId` que recuerda el resultado (incluidos los rechazos)
  y el estado de emisión, con un *claim* con *lease* de 30 s para que solo un procesador emita; `CorrelationId` acotado a 64 caracteres
  con índice único también en `Transfer`.
- **Transaction**: máquina de estados con transiciones válidas solo desde `Pending`, mediante actualización condicional atómica,
  emitiendo antes de confirmar la transición para no perder la recuperación; los estados finales se ignoran sin eventos.
- **Constancia** de mensajes ignorados con registro estructurado; **pruebas automáticas** con SQL Server real en contenedor, un proyecto
  de pruebas por servicio.
- Sin cambios en nombres de eventos ni en payloads.

## Technical Context

**Language/Version**: C# / .NET 10 (`net10.0`), nullable e implicit usings habilitados

**Primary Dependencies**: ASP.NET Core (Web SDK), EF Core 10.0.12 (SqlServer), Azure.Messaging.ServiceBus 7.21.0, MediatR 14.2.0, Newtonsoft.Json 13.0.4. Pruebas: xUnit, Microsoft.NET.Test.Sdk y Testcontainers para SQL Server (versiones por fijar al implementar; no verificadas en esta sesión)

**Storage**: SQL Server (una base por servicio: `BankTransfer`, `BankTransaction`); migraciones de EF Core por servicio

**Testing**: xUnit con SQL Server real en contenedor (Docker) y un doble de `IServiceBusSenderService`; un proyecto por servicio. Hoy no existe proyecto de pruebas

**Target Platform**: servicios .NET en el host (local con `dotnet run --launch-profile http`), con emulador de Azure Service Bus y SQL Server en contenedores

**Project Type**: microservicios dirigidos por eventos (solución `Bank.slnx`); aquí se modifican dos servicios

**Performance Goals**: sin objetivo cuantitativo propio; el costo añadido por mensaje es una consulta indexada y de una a tres escrituras pequeñas

**Constraints**: sin referencias de proyecto ni HTTP entre servicios; sin cambios de contrato de eventos; migraciones aplicadas por comando; reintentos inmediatos de la mensajería (hallazgo 1) siguen vigentes; `LockDuration` de las suscripciones = 1 min

**Scale/Scope**: dos servicios (Transfer y Transaction), 2 migraciones, 1 tabla nueva, 2 proyectos de pruebas; Balance, Notification y Gateway fuera de alcance

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principio / regla | Evaluación previa a la investigación | Reevaluación tras el diseño |
|---|---|---|
| I. Desacoplados solo por eventos | ✅ Solo cambia el comportamiento ante eventos; sin referencias ni HTTP entre servicios | ✅ Cada proyecto de pruebas referencia únicamente a su servicio; sin cambios de contrato |
| II. Idempotentes por `CorrelationId` | ✅ Es el objetivo de la feature | ✅ Índice único en la base + tabla de solicitudes + *claim*; la búsqueda previa no es la única garantía |
| III. Todo camino de error emite su fallo | ⚠️ Los mensajes sin `CorrelationId` no admiten un evento de fallo correlacionable | ⚠️ Desviación justificada (ver *Complexity Tracking*). Los rechazos permanentes siguen emitiendo `*-failed` y, ahora, **una sola vez** |
| IV. Cada servicio es dueño de su BD | ✅ | ✅ `TransferRequest` y los índices viven en la base de su servicio |
| V. Toda corrección lleva prueba automática | ✅ Se crean los proyectos de pruebas que hoy no existen | ✅ Cada escenario de la spec tiene prueba (ver `quickstart.md`); las pruebas se escriben antes que el código |
| Restricciones técnicas: contrato de eventos | ✅ | ✅ Mismos nombres y campos (`contracts/events.md`); no se toca `Config.json` |
| Restricciones técnicas: secretos | ✅ | ✅ Las pruebas usan credenciales efímeras del contenedor; ningún secreto en el repo |
| Restricciones técnicas: migraciones por comando | ✅ | ✅ Dos migraciones nuevas, aplicadas con `apply-migrations.ps1` |
| Flujo: deuda técnica solo se señala | ✅ | ✅ No se corrigen reintentos sin espera, Outbox ni idempotencia de Balance; se señalan |
| Flujo: sin commits sin orden expresa | ✅ | ✅ |

**Resultado**: ✅ Sin violaciones bloqueantes. Una desviación justificada registrada abajo.

## Project Structure

### Documentation (this feature)

```text
specs/001-idempotent-transfer-processing/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/
│   └── events.md        # Phase 1 output (/speckit-plan command)
├── checklists/
│   └── requirements.md  # Calidad de la especificación (/speckit-specify)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
src/
├── Bank.Transfer/Bank.Transfer.Api/
│   ├── Domain/
│   │   ├── Constants/            # + resultado (Confirmed/Failed) y EMISSION_LEASE
│   │   └── Entities/             # + TransferRequestEntity
│   ├── Application/
│   │   ├── Database/             # IDatabaseService: + DbSet de TransferRequest
│   │   └── Features/Process/     # ProcessService (reescribe TransferInitiated); DatabaseErrors (+ violación de unicidad)
│   └── Persistence/
│       ├── Database/             # DatabaseService: longitud e índices únicos
│       └── Migrations/           # + migración AddIdempotency
└── Bank.Transaction/Bank.Transaction.Api/
    ├── Application/
    │   └── Features/Process/     # ProcessService (máquina de estados); DatabaseErrors (+ violación de unicidad)
    └── Persistence/
        ├── Database/             # DatabaseService: longitud e índice único
        └── Migrations/           # + migración AddCorrelationIdUniqueness

tests/
├── Bank.Transfer.Api.Tests/      # nuevo: escenarios de las historias 1 y 3 y casos límite
└── Bank.Transaction.Api.Tests/   # nuevo: escenarios de la historia 2

Bank.slnx                         # + los dos proyectos de pruebas
CLAUDE.md, README.md              # actualizar: ya existen pruebas y cómo ejecutarlas
```

**Structure Decision**: se respeta la estructura por servicio de `CLAUDE.md` (`Domain/`, `Application/`, `Persistence/`); la lógica va en
`ProcessService` y la entidad nueva en `Domain/Entities`. Los proyectos de pruebas viven en `tests/`, **uno por servicio**, para que
ningún proyecto referencie a más de un servicio.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Principio III: un mensaje sin `CorrelationId` o ilegible se registra y se completa sin emitir `*-failed` | Sin identificador no existe evento de fallo que se pueda correlacionar con ninguna transferencia, y reintentarlo 10 veces es inútil | Mandarlo a la DLQ con motivo `PoisonMessage` es más correcto, pero exige modificar el receptor de cada servicio (hallazgo 4, otra feature) |
| Dos proyectos de pruebas nuevos en lugar de uno | Mantener el Principio I estricto: ningún proyecto referencia a más de un servicio | Un único proyecto referenciaría Transfer y Transaction a la vez |
| Tabla `TransferRequest` además de `Transfer` | Recordar rechazos y el estado de emisión (historias 1 y 3); un rechazo no deja fila en `Transfer` | Columnas extra en `Transfer` no pueden recordar un rechazo (`research.md` R2) |
