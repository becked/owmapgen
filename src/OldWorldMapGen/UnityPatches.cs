using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace OldWorldMapGen
{
    /// <summary>
    /// Harmony patches to stub out Unity engine methods that require native
    /// Unity runtime (which doesn't exist in our standalone context).
    /// </summary>
    public static class UnityPatches
    {
        private static Harmony harmony;

        private static void EnsureHarmony()
        {
            if (harmony == null)
                harmony = new Harmony("com.owmapgen.unitypatches");
        }

        /// <summary>
        /// Patch Unity engine methods (Debug, Application) that crash without native runtime.
        /// Call before any game code runs.
        /// </summary>
        public static void Apply()
        {
            EnsureHarmony();
            PatchDebugMethods();
            PatchQuit();
        }

        /// <summary>
        /// Patch game methods that call Unity native math functions.
        /// Call after game assemblies are loaded.
        /// </summary>
        public static void ApplyGamePatches()
        {
            EnsureHarmony();
            PatchPerlinNoise();
        }

        private static void PatchDebugMethods()
        {
            foreach (string methodName in new[] { "Log", "LogWarning", "LogError" })
            {
                PatchAllOverloads(typeof(Debug), methodName, nameof(SkipMethod));
            }

            try
            {
                var logHandlerType = typeof(Debug).Assembly.GetType("UnityEngine.DebugLogHandler");
                if (logHandlerType != null)
                {
                    foreach (var method in logHandlerType.GetMethods(
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (method.Name.Contains("Internal_Log") || method.Name == "LogFormat")
                        {
                            try { harmony.Patch(method, prefix: new HarmonyMethod(typeof(UnityPatches), nameof(SkipMethod))); }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private static void PatchQuit()
        {
            PatchAllOverloads(typeof(Application), "Quit", nameof(SkipMethod));
        }

        private static void PatchPerlinNoise()
        {
            // Find NoiseGenerator.GetPerlinOctaves in TenCrowns.GameCore
            Type noiseGenType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "TenCrowns.GameCore") continue;
                noiseGenType = assembly.GetType("TenCrowns.GameCore.NoiseGenerator");
                break;
            }

            if (noiseGenType == null)
            {
                Console.Error.WriteLine("Warning: Could not find NoiseGenerator type for PerlinNoise patch.");
                return;
            }

            var targetMethod = noiseGenType.GetMethod("GetPerlinOctaves",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (targetMethod == null)
            {
                Console.Error.WriteLine("Warning: Could not find GetPerlinOctaves method.");
                return;
            }

            try
            {
                harmony.Patch(targetMethod,
                    transpiler: new HarmonyMethod(typeof(UnityPatches), nameof(TranspilePerlinCalls)));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to transpile GetPerlinOctaves: {ex.Message}");
            }
        }

        /// <summary>
        /// Transpiler: replaces call to Mathf.PerlinNoise with MathfShim.PerlinNoise
        /// </summary>
        static IEnumerable<CodeInstruction> TranspilePerlinCalls(IEnumerable<CodeInstruction> instructions)
        {
            var nativePerlin = typeof(Mathf).GetMethod("PerlinNoise",
                BindingFlags.Static | BindingFlags.Public);
            var shimPerlin = typeof(MathfShim).GetMethod(nameof(MathfShim.PerlinNoise),
                BindingFlags.Static | BindingFlags.Public);

            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call &&
                    instruction.operand is MethodInfo mi &&
                    mi == nativePerlin &&
                    shimPerlin != null)
                {
                    yield return new CodeInstruction(OpCodes.Call, shimPerlin);
                    replaced++;
                }
                else
                {
                    yield return instruction;
                }
            }

            if (replaced > 0)
                Console.Error.WriteLine($"  Patched {replaced} Mathf.PerlinNoise call(s) in NoiseGenerator.");
        }

        private static void PatchAllOverloads(Type type, string methodName, string prefixName)
        {
            try
            {
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public))
                {
                    if (method.Name == methodName)
                    {
                        try { harmony.Patch(method, prefix: new HarmonyMethod(typeof(UnityPatches), prefixName)); }
                        catch { }
                    }
                }
            }
            catch { }
        }

        static bool SkipMethod() => false;
    }

    /// <summary>
    /// Managed replacement for Unity's native Mathf.PerlinNoise.
    /// Uses Ken Perlin's improved noise (2002) with the canonical permutation table.
    /// </summary>
    public static class MathfShim
    {
        public static float PerlinNoise(float x, float y)
        {
            return (PerlinNoise2D(x, y) + 1f) * 0.5f;
        }

        private static float PerlinNoise2D(float x, float y)
        {
            int xi = (int)Math.Floor(x) & 255;
            int yi = (int)Math.Floor(y) & 255;
            float xf = x - (float)Math.Floor(x);
            float yf = y - (float)Math.Floor(y);

            float u = Fade(xf);
            float v = Fade(yf);

            int aa = p[p[xi] + yi];
            int ab = p[p[xi] + yi + 1];
            int ba = p[p[xi + 1] + yi];
            int bb = p[p[xi + 1] + yi + 1];

            float x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
            float x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);

            return Lerp(x1, x2, v);
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        private static float Lerp(float a, float b, float t) => a + t * (b - a);

        private static float Grad(int hash, float x, float y)
        {
            int h = hash & 3;
            float u = h < 2 ? x : y;
            float v = h < 2 ? y : x;
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        private static readonly int[] p = new int[512];

        static MathfShim()
        {
            int[] perm = {
                151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,
                140,36,103,30,69,142,8,99,37,240,21,10,23,190,6,148,
                247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,
                57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,
                74,165,71,134,139,48,27,166,77,146,158,231,83,111,229,122,
                60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,
                65,25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,
                200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,
                52,217,226,250,124,123,5,202,38,147,118,126,255,82,85,212,
                207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,
                119,248,152,2,44,154,163,70,221,153,101,155,167,43,172,9,
                129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,
                218,246,97,228,251,34,242,193,238,210,144,12,191,179,162,241,
                81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,
                4,184,204,176,115,121,50,45,127,4,150,254,138,236,205,93,
                222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180
            };
            for (int i = 0; i < 256; i++)
            {
                p[i] = perm[i];
                p[256 + i] = perm[i];
            }
        }
    }
}
