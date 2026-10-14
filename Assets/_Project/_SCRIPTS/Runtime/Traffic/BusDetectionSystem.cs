using System.Collections.Generic;
using FixMyCity.Buses;
using FixMyCity.Roads;

namespace FixMyCity.Traffic
{
    /// <summary>Resultado de una búsqueda de vecino. Struct → no genera basura.</summary>
    public readonly struct NeighborInfo
    {
        public readonly BusMovementController Bus;
        /// <summary>Hueco libre en metros entre parachoques (negativo = se están solapando).</summary>
        public readonly float Gap;

        public NeighborInfo(BusMovementController bus, float gap)
        {
            Bus = bus;
            Gap = gap;
        }

        public bool Exists => Bus != null;
        public static readonly NeighborInfo None = new NeighborInfo(null, float.PositiveInfinity);
    }

    /// <summary>
    /// "OJOS" de los buses: ¿quién va delante/detrás de mí en tal carril?
    ///
    /// TRUCO DE RENDIMIENTO: el BusTrafficManager mantiene a los buses de cada vía ORDENADOS por
    /// distancia recorrida. Así, "el bus de adelante" es simplemente el siguiente en la lista:
    /// recorremos hacia adelante desde mi índice y paramos en cuanto salimos del rango de detección.
    /// Sin raycasts, sin física, sin recorrer todos los buses del mapa → coste casi O(1) por bus.
    ///
    ///   lista ordenada:  [bus0 | bus1 | YO | bus3 | bus4 | ...]
    ///                                     ──► buscar adelante (índices mayores)
    ///                          ◄── buscar atrás (índices menores)
    /// </summary>
    public static class BusDetectionSystem
    {
        /// <summary>
        /// ¿El bus está (aunque sea en parte) dentro de este carril? A mitad de un cambio de carril
        /// ocupa los dos, que es justo lo que queremos para que nadie se le meta.
        /// </summary>
        public static bool OccupiesLane(BusMovementController bus, BusLane lane, RoadPath road, TrafficRules rules)
        {
            float threshold = road.LaneWidth * rules.laneOccupancyFactor;
            float delta = bus.Data.LateralOffset - road.GetLaneOffset(lane);
            return delta < threshold && delta > -threshold;
        }

        /// <summary>Hueco entre mi parachoques delantero y el trasero de 'leader'.</summary>
        public static float GapBetween(BusMovementController follower, BusMovementController leader)
        {
            return leader.RearDistance - follower.FrontDistance;
        }

        /// <summary>Primer bus por delante de 'index' que ocupa 'lane', dentro de 'range' metros.</summary>
        public static NeighborInfo FindLeaderInLane(List<BusMovementController> sorted, int index, BusLane lane,
                                                    RoadPath road, TrafficRules rules, float range)
        {
            BusMovementController me = sorted[index];
            float maxDistance = me.Distance + range;

            for (int i = index + 1; i < sorted.Count; i++)
            {
                BusMovementController other = sorted[i];
                if (other.Distance > maxDistance) break;
                if (!other.IsActiveOnRoad) continue;
                if (OccupiesLane(other, lane, road, rules))
                    return new NeighborInfo(other, GapBetween(me, other));
            }
            return NeighborInfo.None;
        }

        /// <summary>Primer bus por detrás de 'index' que ocupa 'lane', dentro de 'range' metros.</summary>
        public static NeighborInfo FindFollowerInLane(List<BusMovementController> sorted, int index, BusLane lane,
                                                      RoadPath road, TrafficRules rules, float range)
        {
            BusMovementController me = sorted[index];
            float minDistance = me.Distance - range;

            for (int i = index - 1; i >= 0; i--)
            {
                BusMovementController other = sorted[i];
                if (other.Distance < minDistance) break;
                if (!other.IsActiveOnRoad) continue;
                if (OccupiesLane(other, lane, road, rules))
                    return new NeighborInfo(other, GapBetween(other, me));
            }
            return NeighborInfo.None;
        }

        /// <summary>
        /// El bus de adelante que me IMPORTA para frenar: el primero que comparte algún carril conmigo.
        /// Si estoy a mitad de un cambio de carril, cuento los dos carriles.
        /// </summary>
        public static NeighborInfo FindLeaderInOccupiedLanes(List<BusMovementController> sorted, int index,
                                                             RoadPath road, TrafficRules rules, float range)
        {
            BusMovementController me = sorted[index];
            bool meLeft = OccupiesLane(me, BusLane.Left, road, rules);
            bool meRight = OccupiesLane(me, BusLane.Right, road, rules);
            float maxDistance = me.Distance + range;

            for (int i = index + 1; i < sorted.Count; i++)
            {
                BusMovementController other = sorted[i];
                if (other.Distance > maxDistance) break;
                if (!other.IsActiveOnRoad) continue;

                bool shares = (meLeft && OccupiesLane(other, BusLane.Left, road, rules)) ||
                              (meRight && OccupiesLane(other, BusLane.Right, road, rules));
                if (shares) return new NeighborInfo(other, GapBetween(me, other));
            }
            return NeighborInfo.None;
        }

        /// <summary>
        /// CORTESÍA / "DIRECCIONAL PUESTA": busca adelante un bus que se está metiendo (o espera meterse)
        /// al carril de puertas desde el expreso. Los buses del carril de puertas que vienen detrás lo
        /// tratan como si ya estuviera en su carril:
        ///   a) Ya decidió cambiarse (TargetLane = Left) pero aún no ocupa el carril → está cruzando.
        ///      Sin esto, el de atrás no lo "vería" hasta que ya estuviera dentro → frenazo o choque.
        ///   b) Quiere meterse para su paradero pero aún no hay hueco, y va casi detenido → le cedo el paso.
        ///      Sin esto, un bus que llega a su paradero por la derecha podría esperar para siempre
        ///      mientras la cola de la izquierda avanza (livelock).
        /// </summary>
        public static NeighborInfo FindMergerAhead(List<BusMovementController> sorted, int index, RoadPath road,
                                                   TrafficRules rules, float range, float maxMergerSpeed)
        {
            BusMovementController me = sorted[index];
            float maxDistance = me.Distance + range;

            for (int i = index + 1; i < sorted.Count; i++)
            {
                BusMovementController other = sorted[i];
                if (other.Distance > maxDistance) break;
                if (!other.IsActiveOnRoad) continue;

                BusMovementData d = other.Data;
                bool alreadyInDoorsLane = OccupiesLane(other, BusLane.Left, road, rules);
                if (alreadyInDoorsLane) continue; // ese caso ya lo cubre la búsqueda normal de líder

                bool crossingIn = d.TargetLane == BusLane.Left;
                bool waitingToMerge = d.WantsDoorsLane && d.TargetLane == BusLane.Right && other.Speed <= maxMergerSpeed;
                if (!crossingIn && !waitingToMerge) continue;

                float gap = GapBetween(me, other);
                if (gap > 0f) return new NeighborInfo(other, gap);
            }
            return NeighborInfo.None;
        }

        /// <summary>
        /// ¿Puedo meterme en 'targetLane' sin cerrarle el paso a nadie?
        ///  - Hueco con el de ADELANTE en ese carril: mi distancia mínima + medio segundo a mi velocidad + extra si me acerco.
        ///  - Hueco con el de ATRÁS: su distancia mínima + lo que recorre en ~0.6 s + extra si viene más rápido que yo.
        /// Es una versión simplificada del modelo MOBIL usado en simuladores de tráfico.
        /// </summary>
        public static bool IsLaneChangeSafe(List<BusMovementController> sorted, int index, BusLane targetLane,
                                            RoadPath road, TrafficRules rules)
        {
            BusMovementController me = sorted[index];
            BusStats myStats = me.Stats;

            NeighborInfo ahead = FindLeaderInLane(sorted, index, targetLane, road, rules, rules.detectionRange);
            if (ahead.Exists)
            {
                float closing = me.Speed - ahead.Bus.Speed;
                float required = myStats.minimumGap + me.Speed * 0.5f + (closing > 0f ? closing : 0f);
                if (ahead.Gap < required) return false;
            }

            NeighborInfo behind = FindFollowerInLane(sorted, index, targetLane, road, rules, rules.detectionRange);
            if (behind.Exists)
            {
                BusMovementController f = behind.Bus;
                float closing = f.Speed - me.Speed;
                // Si el de atrás está detenido (p. ej. me está cediendo el paso) basta con medio hueco mínimo.
                float baseGap = f.Speed < rules.stoppedSpeedThreshold ? f.Stats.minimumGap * 0.5f : f.Stats.minimumGap;
                float required = baseGap + f.Speed * 0.6f + (closing > 0f ? closing * 1.5f : 0f);
                if (behind.Gap < required) return false;
            }

            return true;
        }

        /// <summary>
        /// ¿Hay espacio libre en (distance, lane) para colocar un bus? Lo usa el spawner para no
        /// aparecer encima de otro bus. Recorre la lista completa, pero solo se llama al spawnear.
        /// </summary>
        public static bool IsSpaceFree(List<BusMovementController> sorted, float distance, float halfLength,
                                       BusLane lane, RoadPath road, TrafficRules rules, float clearance)
        {
            if (sorted == null) return true;
            float myFront = distance + halfLength;
            float myRear = distance - halfLength;

            for (int i = 0; i < sorted.Count; i++)
            {
                BusMovementController other = sorted[i];
                if (!other.IsActiveOnRoad) continue;
                if (!OccupiesLane(other, lane, road, rules)) continue;
                bool overlaps = other.RearDistance < myFront + clearance && other.FrontDistance > myRear - clearance;
                if (overlaps) return false;
            }
            return true;
        }
    }
}
