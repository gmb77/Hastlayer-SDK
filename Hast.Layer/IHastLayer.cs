using Hast.Common.Services;
using Hast.Communication.Services;
using Hast.Layer.Extensibility.Events;
using Hast.Transformer.Abstractions.SimpleMemory;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hast.Layer
{
    public interface IHastlayer : IDisposable
    {
        /// <summary>
        /// Occurs when the member invocation (e.g. a method call) was transferred to hardware and finished there.
        /// </summary>
        event ExecutedOnHardwareEventHandler ExecutedOnHardware;
        event InvokingEventHandler Invoking;

        /// <summary>
        /// Gets those devices which have their support drivers loaded.
        /// </summary>
        /// <returns>Those devices which have their support drivers loaded.</returns>
        IEnumerable<IDeviceManifest> GetSupportedDevices();

        /// <summary>
        /// Generates and implements a hardware representation of the given assemblies. Be sure to use assemblies built
        /// with the Debug configuration.
        /// </summary>
        /// <param name="assemblyPaths">The assemblies' paths that should be implemented as hardware.</param>
        /// <param name="configuration">Configuration for how the hardware generation should happen.</param>
        /// <returns>The representation of the assemblies implemented as hardware.</returns>
        /// <exception cref="HastlayerException">
        /// Thrown if any lower-level exception or other error happens during hardware generation.
        /// </exception>
        Task<IHardwareRepresentation> GenerateHardware(
            IEnumerable<string> assemblyPaths,
            IHardwareGenerationConfiguration configuration);

        /// <summary>
        /// Generates a proxy for the given object that will transfer suitable calls to the hardware implementation.
        /// </summary>
        /// <typeparam name="T">Type of the object to generate a proxy for.</typeparam>
        /// <param name="hardwareRepresentation">The representation of the assemblies implemented as hardware.</param>
        /// <param name="hardwareObject">The object to generate the proxy for.</param>
        /// <param name="configuration">
        /// Configuration for how the proxy generation should happen. If null, it will be substituted with
        /// <see cref="ProxyGenerationConfiguration.Default"/></param>
        /// <returns>The generated proxy object.</returns>
        /// <exception cref="HastlayerException">
        /// Thrown if any lower-level exception or other error happens during proxy generation.
        /// </exception>
        Task<T> GenerateProxy<T>(
            IHardwareRepresentation hardwareRepresentation,
            T hardwareObject,
            IProxyGenerationConfiguration configuration = null) where T : class;

        /// <summary>
        /// Gets a registered service for the interface <typeparamref name="TServiceInterface"/> inside a disposable
        /// container that maintains an independent dependency injection scope as long as the host <see
        /// cref="IHastlayer"/> object isn't disposed.
        /// </summary>
        /// <returns>The matching communication service.</returns>
        /// <remarks>
        /// <para>
        /// If you need a communication service, use this to request an <see cref="ICommunicationServiceSelector"/>.
        /// </para>
        /// </remarks>
        DisposableContainer<TServiceInterface> GetService<TServiceInterface>();

        /// <summary>
        /// Constructs a new <see cref="SimpleMemory"/> object that represents a simplified memory model available on
        /// the FPGA for transformed algorithms.
        /// </summary>
        /// <param name="configuration">The configuration with the device information.</param>
        /// <param name="cellCount">
        /// The number of memory "cells". The memory is divided into independently accessible "cells"; the size of the
        /// allocated memory space is calculated from the cell count and the cell size indicated in
        /// <see cref="SimpleMemory.MemoryCellSizeBytes"/>.
        /// </param>
        /// <returns>A new instance of <see cref="SimpleMemory"/></returns>
        SimpleMemory CreateMemory(IHardwareGenerationConfiguration configuration, int cellCount);

        /// <summary>
        /// Constructs a new <see cref="SimpleMemory"/> object that represents a simplified memory model available on
        /// the FPGA for transformed algorithms. Use this method only if you have no control over the source of the
        /// input data or if the generation process can't be done through Memory&lt;byte&gt; access alone. (E.g. if
        /// using an API that only supports classic array manipulation) Otherwise prefer to use the other overload and
        /// produce the content using Memory&lt;byte&gt; operations. Use <see cref="SimpleMemoryAccessor"/> to gain
        /// access to the internal Memory&lt;byte&gt;.
        /// </summary>
        /// <param name="configuration">The configuration with the device information.</param>
        /// <param name="data">
        /// The input data which is referenced by or copied into the <see cref="SimpleMemory"/> depending on the
        /// selected device's characteristics, particularly its <see cref="MemoryConfiguration{T}.MinimumPrefix"/>.
        /// </param>
        /// <param name="withPrefixCells">The amount of empty header space reserved in the <see cref="data"/>.</param>
        /// <returns>A new instance of <see cref="SimpleMemory"/></returns>
        // Here we use Memory&lt;byte&gt; because xmldoc doesn't support generics with a definite type in <see> and in
        // the generated documentation they get replaced with <T>. See https://stackoverflow.com/a/41208166.
        SimpleMemory CreateMemory(IHardwareGenerationConfiguration configuration, Memory<byte> data, int withPrefixCells = 0);
    }
}
