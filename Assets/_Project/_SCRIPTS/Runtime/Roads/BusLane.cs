namespace FixMyCity.Roads
{
    /// <summary>
    /// Los dos carriles de la vía exclusiva del Metropolitano.
    ///
    ///  Left  (0) = carril de PUERTAS / paradas. Pegado a los paraderos, más lento (prudencia).
    ///  Right (1) = carril EXPRESO. Lejos de las puertas, permite ir a velocidad máxima.
    ///
    /// Es un byte porque se guarda por bus y se compara miles de veces por segundo:
    /// cuanto más pequeño el dato, mejor para la caché del celular.
    /// </summary>
    public enum BusLane : byte
    {
        Left = 0,
        Right = 1
    }
}
