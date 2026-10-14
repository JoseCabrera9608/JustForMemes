# Sistema de movimiento de buses — Fase 1

## Cómo probarlo (2 minutos)

1. `git pull` de la rama `BusBehaviour` y abrir `Assets/_Project/Scenes/PrototipeScene.unity`.
2. Menú **Fix My City → Setup Bus Test Scene** (una sola vez; es idempotente y se deshace con Ctrl+Z).
3. Guardar la escena (Ctrl+S) y darle **Play** con los **Gizmos activados** en la vista Game.

Qué deberías ver:

| Ruta | Paradas | Qué demuestra |
|---|---|---|
| **A** | JP-N1 → CA-N1 | Tras embarcar sale al carril expreso y vuelve al de puertas antes de Canadá |
| **B** | JP-N2 → CA-N2 | Se pone en cola detrás del bus que embarca en N1, o lo adelanta si cabe |
| **SX** | solo CA-N2 | **Video 1**: pasa Javier Prado por el carril expreso sin frenar |
| **C-Lenta** | solo JP-N2 (embarque x3) | Bus lento que provoca colas y adelantamientos (**Video 2**) |

Colores de los gizmos: **rojo** = va al carril de puertas · **verde** = expreso · **magenta** = adelantando · **amarillo** = embarcando · línea **cian** = bus al que está siguiendo.

## Arquitectura

```
_SCRIPTS/
├─ Runtime/                      (asmdef FixMyCity.Runtime)
│  ├─ Roads/     RoadPath        vía 2 carriles = polilínea + offsets laterales
│  │             BusLane         enum Left(puertas)=0 / Right(expreso)=1
│  ├─ Stations/  Station         conjunto de paraderos (Small/Large, central)
│  │             BusStop         paradero; proyecta su posición sobre la vía
│  ├─ Routes/    BusRoute        vía + lista de paraderos (clase serializable)
│  │             RouteManager    despacha buses por ruta (pool + traffic)
│  ├─ Bus/       BusStats        ScriptableObject: datos del MODELO de bus
│  │             BusMovementData estado runtime (distance, speed, lateral…)
│  │             BusMovementController  IDM + lateral + paradas (sin Update)
│  │             BusLaneSystem   CEREBRO de carriles: reglas puras R1–R5
│  ├─ Traffic/   BusTrafficManager  único Update: Sense → Integrate
│  │             BusDetectionSystem "ojos": vecinos por lista ordenada
│  │             TrafficRules    reglamento global (tunable en Play)
│  └─ Pooling/   BusPool         UnityEngine.Pool.ObjectPool + prewarm
└─ Editor/                       (asmdef FixMyCity.Editor)
   └─ BusTestSceneSetup          menú que monta la escena de prueba
```

### Decisiones clave (y por qué)

- **Sin física.** Cada bus es `(distance, lateral)` sobre una vía. Posición = eje(distance) + derecha·lateral. Cero Rigidbody/raycasts → barato en celular y determinista.
- **Un solo `Update`** (BusTrafficManager) en dos fases: *Sense* (todos leen) → *Integrate* (todos escriben). El resultado no depende del orden de la jerarquía.
- **Lista ordenada por distancia.** "¿Quién va delante?" = siguiente en la lista. Insertion sort ≈ O(n) porque casi nunca cambian de orden.
- **IDM** (Intelligent Driver Model) para acelerar/frenar. El paradero es un "bus invisible detenido": el mismo modelo frena suave y exacto.
- **Lateral limitado por la velocidad de avance** (ángulo de giro): un bus detenido no se desliza de lado; el morro gira al cambiar de carril.
- **"Querer" ≠ "poder".** `BusLaneSystem` decide el deseo; `BusDetectionSystem.IsLaneChangeSafe` comprueba el hueco.
- **Sin singletons.** Dependencias por inspector (RouteManager → BusPool, BusTrafficManager).
- **Eventos** en el bus (`BoardingStarted/Completed`, `StopMissed`, `RouteCompleted`) para conectar pasajeros, satisfacción y UI sin acoplar.

### Reglas de carril (BusLaneSystem)

1. **R1** Embarcando → carril de puertas.
2. **R2** Próximo paradero lejos (> `laneReturnDistance`) → expreso. *(Video 1)*
3. **R3** Próximo paradero cerca → carril de puertas.
4. **R4** …salvo que haya un bus **detenido** delante en el carril de puertas y quepa adelantarlo y volver a meterse antes de mi paradero (largo + cambio de carril + frenado + margen). *(Video 2)*
5. **R5** Si hay una cola detenida antes de mi paradero que no puedo adelantar, la distancia de retorno se mide hasta el **final de la cola**.

Más dos reglas de convivencia en el controller: el bus que necesita meterse frena para quedar **detrás** del de la izquierda, y los de la izquierda **ceden el paso** a quien ya puso la direccional (evita choques y bloqueos).

## Parámetros para tunear

- `BUS_SYSTEM → BusTrafficManager → Rules`: `laneReturnDistance` (60), `detectionRange` (80), `mergeMargin`, `minTimeBetweenLaneChanges`…
- `Data/BusStats_Default`: velocidad, aceleración, frenado, `boardingTime`, `maxSteerAngle`, largo del bus.
- `RoadPath_North`: límites de velocidad por carril (puertas 10 m/s, expreso 15 m/s) y offsets de carril.

## Verificado en simulación headless (20 min de juego, 4 rutas)

256 embarques · 0 paraderos perdidos · 0 choques (hueco mínimo 1.2 m) · coste del tick < 0.01 ms con 20 buses (mono, PC).

## Pendiente (siguientes fases)

- Ida y vuelta con giro en U en estación central (grafo de vías).
- Sistema de pasajeros conectado a `BoardingStarted/Completed` (el tiempo de embarque dependerá de la gente).
- Curvas suaves en la vía (hoy es polilínea; el giro visual ya está suavizado).
