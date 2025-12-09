using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MMMapEditor
{
    // Расширение Random для плавающего числа
    public static class RandomExtensions
    {
        public static float NextFloat(this Random random, float min, float max)
        {
            return (float)(min + random.NextDouble() * (max - min));
        }
    }
}
