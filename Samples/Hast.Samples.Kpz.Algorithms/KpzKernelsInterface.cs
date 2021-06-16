using Hast.Layer;
using Hast.Transformer.Abstractions.SimpleMemory;
using System.Diagnostics.CodeAnalysis;

namespace Hast.Samples.Kpz.Algorithms
{
    /// <summary>
    /// This is an implementation of the KPZ algorithm for FPGAs through Hastlayer, storing the whole table in the BRAM
    /// or LUT RAM of the FPGA, thus it can only handle small table sizes.
    /// <see cref="KpzKernelsInterface"/> contains the entry points for the algorithms to be ran on the FPGA.
    /// </summary>
    public class KpzKernelsInterface
    {
        /// <summary>
        /// Calling this function on the host starts the KPZ algorithm.
        /// </summary>
        public virtual void DoIterations(SimpleMemory memory)
        {
            var kernels = new KpzKernels();
            kernels.CopyFromSimpleMemoryToRawGrid(memory);
            kernels.InitializeParametersFromMemory(memory);
            // Assume that GridWidth and GridHeight are 2^N.
            var numberOfStepsInIteration = kernels.TestMode ? 1 : KpzKernels.GridWidth * KpzKernels.GridHeight;

            for (int j = 0; j < kernels.NumberOfIterations; j++)
            {
                for (int i = 0; i < numberOfStepsInIteration; i++)
                {
                    // We randomly choose a point on the grid. If there is a pyramid or hole, we randomly switch them.
                    kernels.RandomlySwitchFourCells(kernels.TestMode);
                }
            }

            kernels.CopyToSimpleMemoryFromRawGrid(memory);
        }

        /// <summary>
        /// This function is for testing how Hastlayer works by running a simple add operation between memory cells
        /// 0 and 1, and writing the result to cell 2.
        /// </summary>
        public virtual void TestAdd(SimpleMemory memory) => memory.WriteUInt32(2, memory.ReadUInt32(0) + memory.ReadUInt32(1));

        /// <summary>
        /// This function is for testing how Hastlayer works by running a random generator, writing the results into
        /// SimpleMemory.
        /// </summary>
        public void TestPrng(SimpleMemory memory)
        {
            var kernels = new KpzKernels();
            kernels.InitializeParametersFromMemory(memory);
            const int numberOfStepsInIteration = KpzKernels.GridWidth * KpzKernels.GridHeight;
            for (int i = 0; i < numberOfStepsInIteration; i++)
            {
                memory.WriteUInt32(i, kernels.Random1.NextUInt32());
            }
        }

        /// <summary>
        /// This function adds two numbers on the FPGA using <see cref="TestAdd(SimpleMemory)"/>.
        /// </summary>
        public uint TestAddWrapper(
            IHastlayer hastlayer,
            IHardwareGenerationConfiguration configuration,
            uint a,
            uint b)
        {
            var sm = hastlayer.CreateMemory(configuration, 3);
            sm.WriteUInt32(0, a);
            sm.WriteUInt32(1, b);
            TestAdd(sm);
            return sm.ReadUInt32(2);
        }

        /// <summary>
        /// This function generates random numbers on the FPGA using
        /// <see cref="TestPrng(SimpleMemory)"/>.
        /// </summary>
        public uint[] TestPrngWrapper(
            IHastlayer hastlayer,
            IHardwareGenerationConfiguration configuration)
        {
            var numbers = new uint[KpzKernels.GridWidth * KpzKernels.GridHeight];
            var sm = hastlayer.CreateMemory(configuration, KpzKernels.SizeOfSimpleMemory);

            CopyParametersToMemory(sm, false, 0x_5289_a3b8_9ac5_f211, 0x_5289_a3b8_9ac5_f211, 0);

            TestPrng(sm);

            for (int i = 0; i < KpzKernels.GridWidth * KpzKernels.GridHeight; i++)
            {
                numbers[i] = sm.ReadUInt32(i);
            }

            return numbers;
        }

        /// <summary>
        /// This function pushes parameters and PRNG seed to the FPGA.
        /// </summary>
        public static void CopyParametersToMemory(
            SimpleMemory memoryDst,
            bool testMode,
            ulong randomSeed1,
            ulong randomSeed2,
            uint numberOfIterations)
        {
            const ulong bitMask = 0x_FFFF_FFFF;

            memoryDst.WriteUInt32(KpzKernels.MemIndexRandomStates, (uint)(randomSeed1 & bitMask));
            memoryDst.WriteUInt32(KpzKernels.MemIndexRandomStates + 1, (uint)((randomSeed1 >> 32) & bitMask));
            memoryDst.WriteUInt32(KpzKernels.MemIndexRandomStates + 2, (uint)(randomSeed2 & bitMask));
            memoryDst.WriteUInt32(KpzKernels.MemIndexRandomStates + 3, (uint)((randomSeed2 >> 32) & bitMask));
            memoryDst.WriteUInt32(KpzKernels.MemIndexStepMode, testMode ? 1U : 0U);
            memoryDst.WriteUInt32(KpzKernels.MemIndexNumberOfIterations, numberOfIterations);
        }

        /// <summary>
        /// This is a wrapper for running the KPZ algorithm on the FPGA.
        /// </summary>
        public void DoIterationsWrapper(IHastlayer hastlayer, DoIterationsContext context)
        {
            var sm = hastlayer.CreateMemory(context.Configuration, KpzKernels.SizeOfSimpleMemory);

            if (context.PushToFpga)
            {
                CopyParametersToMemory(sm, context.TestMode, context.RandomSeed1, context.RandomSeed2, context.NumberOfIterations);
                CopyFromGridToSimpleMemory(context.HostGrid, sm);
            }

            DoIterations(sm);
            CopyFromSimpleMemoryToGrid(context.HostGrid, sm);
        }

        /// <summary>Push table into FPGA.</summary>
        private static void CopyFromGridToSimpleMemory(KpzNode[,] gridSrc, SimpleMemory memoryDst)
        {
            for (int x = 0; x < KpzKernels.GridHeight; x++)
            {
                for (int y = 0; y < KpzKernels.GridWidth; y++)
                {
                    var node = gridSrc[x, y];
                    memoryDst.WriteUInt32(KpzKernels.MemIndexGrid + (y * KpzKernels.GridWidth) + x, node.SerializeToUInt32());
                }
            }
        }

        /// <summary>Pull table from the FPGA.</summary>
        private static void CopyFromSimpleMemoryToGrid(KpzNode[,] gridDst, SimpleMemory memorySrc)
        {
            for (int x = 0; x < KpzKernels.GridWidth; x++)
            {
                for (int y = 0; y < KpzKernels.GridHeight; y++)
                {
                    gridDst[x, y] = KpzNode.DeserializeFromUInt32(memorySrc.ReadUInt32(KpzKernels.MemIndexGrid + (y * KpzKernels.GridWidth) + x));
                }
            }
        }

        public class DoIterationsContext
        {
            public IHardwareGenerationConfiguration Configuration { get; set; }

            /// <summary>
            /// Gets or sets the grid of initial <see cref="KpzNode"/> items for the algorithm to work on.
            /// </summary>
            [SuppressMessage(
                "Performance",
                "CA1819:Properties should not return arrays",
                Justification = "There isn't really an alternative for returning non-jagged 2D arrays without a significant overhead.")]
            public KpzNode[,] HostGrid { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether this parameter is false, the FPGA will work
            /// on the grid currently available in it, instead of the grid in the <see cref="HostGrid"/>.
            /// </summary>
            public bool PushToFpga { get; set; }

            /// <summary>
            ///     <para>Gets or sets a value indicating whether it should always switch cells.</para>
            ///     <list type="bullet">
            ///         <item>
            ///             <description>
            ///                 If <see langword="true"/>, <see cref="KpzKernels.RandomlySwitchFourCells(bool)"/> always switches the cells if it finds an adequate place,
            ///             </description>
            ///         </item>
            ///         <item>
            ///             <description>
            ///                 It also does only a single poke, then sends the grid back to the host so that the algorithm can be analyzed in the step-by-step window.
            ///             </description>
            ///         </item>
            ///     </list>
            /// </summary>
            public bool TestMode { get; set; }

            /// <summary>
            /// Gets or sets a random seed for the algorithm.
            /// </summary>
            public ulong RandomSeed1 { get; set; }

            /// <summary>
            /// Gets or sets another random seed for the algorithm.
            /// </summary>
            public ulong RandomSeed2 { get; set; }

            /// <summary>
            /// Gets or sets the number of iterations to perform.
            /// </summary>
            public uint NumberOfIterations { get; set; }
        }
    }
}