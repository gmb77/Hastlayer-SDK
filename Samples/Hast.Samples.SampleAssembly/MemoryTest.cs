﻿using Hast.Layer;
using Hast.Synthesis.Abstractions;
using Hast.Transformer.Abstractions.SimpleMemory;

namespace Hast.Samples.SampleAssembly
{
    /// <summary>
    /// A sample that tests the memory access on an FPGA. It'll start incrementing all SimpleMemory cells' values
    /// starting at the given start index until the given length.
    ///
    /// This is a bit more complex version of <see cref="Loopback"/>.
    /// </summary>
    public class MemoryTest
    {
        private const int Run_StartIndexInt32Index = 0;
        private const int Run_LengthInt32Index = 1;


        public virtual void Run(SimpleMemory memory)
        {
            var startIndex = memory.ReadInt32(Run_StartIndexInt32Index);
            var length = memory.ReadInt32(Run_LengthInt32Index);
            var endIndex = startIndex + length;

            for (int i = startIndex; i < endIndex; i++)
            {
                // Adding 1 to the input so it's visible whether this actually has run, not just the untouched data was
                // sent back.
                memory.WriteInt32(i, memory.ReadInt32(i) + 1);
            }
        }


        public int Run(int startIndex, int length, IHastlayer hastlayer = null, IHardwareGenerationConfiguration configuration = null)
        {
            var cellCount = startIndex + length < 2 ? 2 : startIndex + length;
            var memory = hastlayer is null
                ? SimpleMemory.CreateSoftwareMemory(cellCount)
                : hastlayer.CreateMemory(configuration, cellCount);
            memory.WriteInt32(Run_StartIndexInt32Index, startIndex);
            memory.WriteInt32(Run_LengthInt32Index, length);
            Run(memory);
            return memory.ReadInt32(Run_StartIndexInt32Index);
        }
    }
}
