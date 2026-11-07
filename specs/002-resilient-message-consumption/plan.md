# Implementation Plan: Consumo resiliente de mensajes

**Branch**: `002-resilient-message-consumption` (rama no creada; el trabajo continúa en `feat/retry-and-poison-messages`) | **Date**: 2026-11-07 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/002-resilient-message-consumption/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Requisito principal: los cuatro servicios que consumen mensajes (Transaction, Balance, Transfer, Notification) deben **distinguir
mensajes venenosos, fallos permanentes de datos y fallos transitorios**; apartar los venenosos a la DLQ en la primera entrega con
motivo y detalle; **reintentar los transitorios con espera creciente** en lugar de agotar las 10 entregas en segundos; **compensar**
(`*-failed`) y apartar al agotarse los intentos; y registrar todo con `CorrelationId`.

Enfoque técnico (detalle y evidencia en [research.md](research.md)):
- **Una capa de resiliencia por servicio** (`Application/Resilience/`), sin tocar `ProcessService`: un procesador que decide
  (completar / apartar / reintentar) a partir de una clasificación, y un adaptador fino en `External/ServiceBusRecive` que ejecuta esas
  decisiones con el SDK. Así las 103 pruebas existentes no cambian.
- **Mensaje venenoso**: validación previa a MediatR (cuerpo vacío, JSON ilegible o `null`, sin `CorrelationId`, más de 64 caracteres).
  `DeadLetterMessageAsync` con motivo `PoisonMessage`, detalle y `CorrelationId` como propiedad.
- **Espera creciente**: se **programa una copia** del mensaje (`ScheduleMessageAsync`) con el contador `RetryAttempt` y se completa el
  original. 5 reintentos con espera `min(5 s · 3^(n-1), 5 min)` ±20 % de variación (ventana nominal ≈ 8 min). **Probado en el
  emulador** ([PRUEBA] en research.md): la entrega programada funciona.
- **Reintentos agotados**: se emite el `*-failed` del primer paso (Transaction, Balance, Transfer) y se aparta con motivo
  `RetriesExhausted`. Notification y los manejadores que solo actualizan estado no tienen evento de fallo: se registran y se apartan.
- **`ProcessError`** solo registra (origen, entidad, excepción); no aparta ni completa.
- **Pruebas automáticas** para la lógica de decisión (sin broker, con dobles) y para la compensación; dos proyectos de pruebas nuevos
  (Balance con SQL Server en contenedor, Notification sin base de datos), y ampliación de la CI.
- **Regla de seguridad de la compensación (FR-016)**: antes de emitir un `*-failed`, el compensador consulta el registro del
  `CorrelationId` en la base del propio servicio; si ya existe, no emite (evita cancelar sagas sanas o mandar correos de fallo falsos).
  Si no hay registro o la base no responde, emite.
- Sin cambios de esquema, de nombres de eventos, de payloads ni de `infra/local/Config.json`.

## Technical Context

**Language/Version**: C# / .NET 10 (`net10.0`), nullable e implicit usings habilitados

**Primary Dependencies**: Azure.Messaging.ServiceBus 7.21.0, MediatR 14.2.0, Newtonsoft.Json 13.0.4 (lectura), System.Text.Json (emisión). **Sin dependencias nuevas** en los servicios. Pruebas: xUnit y los paquetes ya usados en los proyectos de Transfer y Transaction

**Storage**: N/A. No hay tablas ni migraciones nuevas; el estado del reintento viaja en una propiedad del mensaje

**Testing**: xUnit. La lógica de decisión se prueba con dobles de `IMessageActions` e `IFailureCompensator` y un delegado `process` (sin broker ni base de datos). El compensador (FR-016) se prueba con SQL Server real en contenedor en Transaction, Balance y Transfer; Balance obtiene un proyecto nuevo con Testcontainers (Docker) y Notification un proyecto nuevo sin Docker

**Target Platform**: servicios .NET en el host (`dotnet run --launch-profile http`) con emulador de Azure Service Bus y SQL Server en contenedores

**Project Type**: microservicios dirigidos por eventos (`Bank.slnx`); se modifican cuatro servicios

**Performance Goals**: sin objetivo cuantitativo propio; el coste por mensaje sano es una validación de JSON en memoria. Un reintento cuesta un envío programado y una finalización

**Constraints**: sin referencias de proyecto ni HTTP entre servicios (Principio I) ⇒ el código de resiliencia se **duplica por servicio**; sin cambios de contrato de eventos; `LockDuration` = 1 min, `MaxDeliveryCount` = 10 y TTL = 1 h se conservan (red de seguridad); el emulador limita el TTL a 1 h, por lo que la ventana de ≈ 8 min cabe de sobra

**Scale/Scope**: 4 servicios, ≈ 7 archivos nuevos por servicio, 2 proyectos de pruebas nuevos, 2 jobs nuevos en la CI, 1 enmienda menor de la constitución

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principio / regla | Evaluación previa a la investigación | Reevaluación tras el diseño |
|---|---|---|
| I. Desacoplados solo por eventos | ⚠️ El código común no puede compartirse en un proyecto | ✅ Se duplica por servicio (ver *Complexity Tracking*); sin referencias ni HTTP; los reintentos usan el mismo tópico |
| II. Idempotentes por `CorrelationId` | ⚠️ Balance y Notification no lo son y recibirán reintentos | ⚠️ Riesgo señalado, **no empeora**: hoy ya reciben hasta 10 entregas; con espera son 6. Sin cambios en su lógica (deuda: hallazgo 5) |
| III. Todo camino de error emite su fallo | ⚠️ Es el objetivo, pero no todos los manejadores tienen evento de fallo | ✅ Cubre los primeros pasos (`*-initiated`) y los venenosos con `CorrelationId`. ⚠️ Los manejadores de solo-actualización quedan registrados y apartados, sin compensar (hallazgo 6; ver *Complexity Tracking*) |
| IV. Cada servicio es dueño de su BD | ✅ | ✅ Sin esquema nuevo. El compensador **lee** solo la base de su propio servicio (FR-016) |
| V. Toda corrección lleva prueba automática | ✅ | ✅ Cada escenario de la spec tiene prueba (ver `quickstart.md`); se escriben antes que el código |
| Restricciones: contrato de eventos | ✅ | ✅ Mismos nombres y payloads; solo se añade una propiedad de aplicación (`RetryAttempt`) en los mensajes reprogramados ([contracts/message-handling.md](contracts/message-handling.md)) |
| Restricciones: estructura por servicio | ✅ | ✅ `Application/Resilience/` y `External/ServiceBusRecive/`; `Program.cs` solo registra dependencias |
| Restricciones: secretos | ✅ | ✅ Ninguno nuevo; la cadena del emulador ya está documentada |
| Flujo: cambio de evento exige actualizar Config.json y README | ✅ | ✅ No se añaden ni renombran eventos |
| Flujo: PR con CI en verde y checks exigidos | ⚠️ | ⚠️ Se añaden dos jobs (`Tests (Balance)`, `Tests (Notification)`): requieren **enmienda de la constitución** y **ajustar la protección de ramas** (paso manual en la web de GitHub) |
| Flujo: deuda técnica solo se señala | ✅ | ✅ Outbox, reconciliador, circuit breaker, idempotencia de Balance/Notification y `ServiceBusSender` sin dispose se señalan, no se corrigen |
| Flujo: sin commits sin orden expresa | ✅ | ✅ |

**Resultado**: ✅ Sin violaciones bloqueantes. Dos desviaciones justificadas (abajo) y dos pasos manuales identificados.

## Project Structure

### Documentation (this feature)

```text
specs/002-resilient-message-consumption/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/
│   └── message-handling.md   # Phase 1 output (/speckit-plan command)
├── checklists/
│   └── requirements.md  # Calidad de la especificación (/speckit-specify)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
src/
├── Bank.Transaction/Bank.Transaction.Api/      # misma forma en Balance y Transfer
│   ├── Domain/Constants/                       # + DeadLetterReasonConstants, RetryConstants
│   ├── Application/Resilience/                 # nuevo
│   │   ├── RetryPolicy.cs                      #   máximo de reintentos y espera con variación
│   │   ├── MessageClassifier.cs                #   detecta mensajes venenosos
│   │   ├── IMessageActions.cs                  #   completar / apartar / reprogramar (abstracción del broker)
│   │   ├── IFailureCompensator.cs              #   emitir el *-failed del primer paso
│   │   ├── FailureCompensator.cs               #   implementación: consulta el registro propio (IServiceScopeFactory → IDatabaseService) y emite con IServiceBusSenderService
│   │   ├── ProcessorErrorHandler.cs            #   registra los errores del procesador (ProcessError)
│   │   └── ResilientMessageProcessor.cs        #   decide qué hacer con cada mensaje
│   ├── External/ServiceBusRecive/
│   │   ├── ServiceBusReciveService.cs          #   se adelgaza; ProcessError registra
│   │   └── ServiceBusMessageActions.cs         #   adaptador sobre ProcessMessageEventArgs
│   └── Program.cs                              #   + registro de DI
├── Bank.Notification/Bank.Notification.Api/    # igual, sin FailureCompensator (registra un compensador nulo)
└── (Bank.Gateway no consume mensajes: sin cambios)

tests/
├── Bank.Transfer.Api.Tests/       # + Resilience/ (decisiones, compensación con SQL real)
├── Bank.Transaction.Api.Tests/    # + Resilience/
├── Bank.Balance.Api.Tests/        # nuevo, con SQL Server en contenedor (solo el compensador lo usa)
└── Bank.Notification.Api.Tests/   # nuevo, sin base de datos

.github/workflows/ci.yml           # + Balance y Notification en la matriz de pruebas
.specify/memory/constitution.md    # enmienda: lista de checks exigidos (1.2.0)
Bank.slnx                          # + los dos proyectos de pruebas
infra/local/duplicate-test/        # opción para enviar un cuerpo arbitrario (mensaje venenoso) en la verificación manual
CLAUDE.md, README.md               # documentar el comportamiento y cómo verificarlo
```

**Structure Decision**: se respeta la estructura por servicio de `CLAUDE.md`. La lógica de decisión vive en `Application/Resilience/`
(independiente del SDK, por eso probable sin broker) y el SDK queda confinado a `External/ServiceBusRecive/`. **`ProcessService` no
se modifica**: la validación previa y la clasificación ocurren antes de MediatR, y la compensación es una clase aparte. Los proyectos
de pruebas siguen siendo **uno por servicio**.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| Principio I: el código de resiliencia se duplica en cuatro servicios | Compartirlo exigiría una referencia de proyecto o un paquete común entre servicios | Una biblioteca compartida (o archivos enlazados) acopla versiones y despliegues, justo lo que el Principio I prohíbe. Es código pequeño y estable; el coste de duplicarlo es bajo |
| Principio III: los manejadores de solo-actualización (p. ej. `balance-confirmed`, `transfer-confirmed` en Transaction; `transfer-confirmed-balance` en Balance; Notification) no emiten `*-failed` al agotar los reintentos | No existe un evento de fallo definido para esos pasos; inventarlo cambia el contrato y el flujo de la saga | Inventar eventos nuevos o un reconciliador excede esta feature (hallazgo 6). Se registran y se apartan con motivo, de modo que la DLQ los explica |
| Dos proyectos de pruebas nuevos y dos jobs de CI nuevos | El Principio V exige pruebas para el comportamiento nuevo de Balance y Notification, y el Principio I limita un proyecto de pruebas a un servicio | Probarlos desde otro proyecto de pruebas referenciaría más de un servicio; no probarlos incumple el Principio V |
| Enmienda de la constitución (1.1.0 → 1.2.0) y cambio manual de la protección de ramas | Los checks exigidos pasan de 3 a 5 y la constitución los nombra expresamente | Dejar los jobs nuevos como informativos permite fusionar con pruebas rotas, lo que contradice la regla de no relajar checks |
