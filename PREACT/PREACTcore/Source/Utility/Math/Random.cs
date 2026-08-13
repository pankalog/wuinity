namespace PREACT.Math
{
    public class Random
    {
        private static System.Random RANDOM = new System.Random();
        private static readonly object LOCK = new object();

        public static void SetSeed(int seed)
        {
            lock (LOCK)
            {
                RANDOM = new System.Random(seed);
            }
        }

        public static float Range(float minInclusive, float maxExlusive)
        {
            lock (LOCK)
            {
                return (float)(minInclusive + RANDOM.NextDouble() * (maxExlusive - minInclusive));
            }
        }

        public static double Range(double minInclusive, double maxExlusive)
        {
            lock (LOCK)
            {
                return minInclusive + RANDOM.NextDouble() * (maxExlusive - minInclusive);
            }
        }

        /// <summary>
        /// Will result in overflow if using full int range as input.
        /// </summary>
        /// <param name="minInclusive"></param>
        /// <param name="maxInclusive"></param>
        /// <returns></returns>
        public static int Range(int minInclusive, int maxExclusive)
        {
            lock (LOCK)
            {
                return RANDOM.Next(minInclusive, maxExclusive);
            }
        }

        /// <summary>
        /// Random float from 0 (inclusive) to 1 (exclusive).
        /// </summary>
        public static float valueF
        {
            get
            {
                lock (LOCK)
                {
                    return (float)RANDOM.NextDouble();
                }
            }
        }

        /// <summary>
        /// Random double from 0 (inclusive) to 1 (exclusive).
        /// </summary>
        public static double valueD
        {
            get
            {
                lock (LOCK)
                {
                    return RANDOM.NextDouble();
                }
            }
        }
    }
}
