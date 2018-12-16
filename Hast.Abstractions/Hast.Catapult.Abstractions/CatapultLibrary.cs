﻿using AdvancedDLSupport;
using IcIWare.NamedIndexers;
using Orchard.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Hast.Catapult.Abstractions
{
    /// <summary>
    /// Job and configuration manager for the Catapult FPGA driver.
    /// </summary>
    public class CatapultLibrary : IDisposable
    {
        private bool _isDisposed = false;
        private IntPtr _handle;

        /// <summary>
        /// Contains the latest tasks to be awaited when starting a new task.
        /// </summary>
        private readonly Task[] _slotDispatch;

        /// <summary>
        /// The slot to be used for the next run.
        /// </summary>
        private int _currentSlot = 0;

        /// <summary>
        /// Gets the interface containing the functions exposed by the native FPGA library.
        /// </summary>
        public ICatapultNativeLibrary NativeLibrary { get; private set; }

        /// <summary>
        /// Gets the device handle utilized by the functions in NativeLibrary.
        /// </summary>
        public IntPtr Handle { get => _handle; }

        /// <summary>
        /// Gets the function used for logging. If null, no logging is done.
        /// </summary>
        public CatapultLogFunction LogFunction { get; private set; }


        /// <summary>
        /// Gets the total number of shell registers.
        /// </summary>
        public int ShellRegisterCount { get; private set; }

        /// <summary>
        /// Gets the number of buffers. When selecting an input buffer the slot index must be between 0 and
        /// (InputBufferCount - 1) inclusive.
        /// </summary>
        public int BufferCount { get; private set; }

        /// <summary>
        /// Gets the maximum length of the message sent to the input buffer. (64kB)
        /// </summary>
        public int BufferSize { get; private set; }

        /// <summary>
        /// Gets whether the PCIe access is enabled on the device.
        /// </summary>
        public bool PcieEnabled
        {
            get
            {
                VerifyResult(NativeLibrary.ReadShellRegister(_handle, 0, out uint pcie));
                return (pcie & Constants.RegisterMaskPcieEnabled) != 0;
            }
            private set
            {
                VerifyResult(NativeLibrary.ReadShellRegister(_handle, 0, out uint pcie));

                // Set control_register[6]
                if (value) pcie |= Constants.RegisterMaskPcieEnabled;
                else pcie &= ~Constants.RegisterMaskPcieEnabled;

                VerifyResult(NativeLibrary.WriteShellRegister(_handle, 0, pcie));
            }
        }

        /// <summary>
        /// Gets or sets the value of the Soft Register. Indexed by memory address.
        /// </summary>
        public readonly NamedIndexer<uint, ulong> SoftRegister;

        /// <summary>
        /// Gets or sets the value of the Shell Register. Indexed by register number.
        /// </summary>
        public readonly NamedIndexer<uint, uint> ShellRegister;

        /// <summary>
        /// Initializes a new instance of the CatapultLibrary class.
        /// </summary>
        /// <param name="libraryPath">The path of the FPGACoreLib DLL file without the extension.</param>
        /// <param name="versionDefinitionsFile">
        /// The location of the version definitions file. If left as null, then "FPGAVersionDefinitions.ini" is expected
        /// in the current working directory.
        /// </param>
        /// <param name="versionManifestFile">
        /// The location of the version manifest file. If left as null, then "FPGADefaultVersionManifest.ini" is expected
        /// in the current working directory.
        /// </param>
        /// <param name="logFunction">
        /// Optional logging function that takes flag values from Hast.Communication.Constants.Constants.Log as its first
        /// parameter and a string as its second. Note that the strings all end with newline character.
        /// </param>
        public CatapultLibrary(
            string libraryPath = Constants.DefaultLibraryPath,
            string versionDefinitionsFile = null,
            string versionManifestFile = null,
            CatapultLogFunction logFunction = null)
        {
            LogFunction = logFunction;

            // Indexer register access
            SoftRegister = new NamedIndexer<uint, ulong>(
                (address) => { VerifyResult(NativeLibrary.ReadSoftRegister(_handle, address, out ulong value)); return value; },
                (address, value) => { VerifyResult(NativeLibrary.WriteSoftRegister(_handle, address, value)); }
            );
            ShellRegister = new NamedIndexer<uint, uint>(
                (register) => { VerifyResult(NativeLibrary.ReadShellRegister(_handle, register, out uint value)); return value; },
                (register, value) => { VerifyResult(NativeLibrary.WriteShellRegister(_handle, register, value)); }
            );

            // Initialize FPGA library and device
            // We don't use dllmap so create ALD without it
            var builderWithoutDllMap = new NativeLibraryBuilder(NativeLibraryBuilder.Default.Options & ~ImplementationOptions.EnableDllMapSupport);
            NativeLibrary = builderWithoutDllMap.ActivateInterface<ICatapultNativeLibrary>(libraryPath);
            // Check if device is available and connect
            VerifyResult(NativeLibrary.IsDevicePresent(versionManifestFile, logFunction));
            var createStatus = NativeLibrary.CreateHandle(out _handle,
                endpointNumber: Constants.PcieHipNumber,
                flags: 0,
                pchVerDefnsFile: string.IsNullOrEmpty(versionDefinitionsFile) ? null : new StringBuilder(versionDefinitionsFile),
                pchVersionManifestFile: string.IsNullOrEmpty(versionManifestFile) ? null : new StringBuilder(versionManifestFile),
                logFunc: logFunction);
            VerifyResult(createStatus);

            // Configure hardware settings & enable hardware
            SoftRegister[Constants.ConfigDram.Channel0] = 0;
            PcieEnabled = true;

            // Query device info
            VerifyResult(NativeLibrary.GetNumberShellRegisters(_handle, out uint count));
            ShellRegisterCount = (int)count;
            VerifyResult(NativeLibrary.GetNumberBuffers(_handle, out count));
            BufferCount = (int)count;
            VerifyResult(NativeLibrary.GetBufferSize(_handle, out count));
            BufferSize = (int)count;

            // Set up the task tracker
            _slotDispatch = new Task[BufferCount];
            for (int i = 0; i < BufferCount; i++) _slotDispatch[i] = Task.CompletedTask;
        }

        public static CatapultLibrary Create(IDictionary<string, object> config, ILogger logger)
        {
            var libraryPath = config.ContainsKey(Constants.ConfigKeys.LibraryPath) ?
                config[Constants.ConfigKeys.LibraryPath] ?? Constants.DefaultLibraryPath :
                Constants.DefaultLibraryPath;
            var versionDefinitionsFile = config.ContainsKey(Constants.ConfigKeys.VersionDefinitionsFile) ?
                config[Constants.ConfigKeys.VersionDefinitionsFile] : null;
            var versionManifestFile = config.ContainsKey(Constants.ConfigKeys.VersionManifestFile) ?
                config[Constants.ConfigKeys.VersionManifestFile] : null;

            return new CatapultLibrary(
                (string)libraryPath,
                (string)versionDefinitionsFile,
                (string)versionManifestFile,
                logFunction: (flagValue, text) =>
                {
                    var flag = (Constants.Log)flagValue;
                    if (flag == Constants.Log.None) return;

                    if (flag.HasFlag(Constants.Log.Debug) || flag.HasFlag(Constants.Log.Verbose))
                        logger.Debug(text);
                    else if (flag.HasFlag(Constants.Log.Info))
                        logger.Information(text);
                    else if (flag.HasFlag(Constants.Log.Error))
                        logger.Error(text);
                    else if (flag.HasFlag(Constants.Log.Fatal))
                        logger.Fatal(text);
                    else if (flag.HasFlag(Constants.Log.Warn))
                        logger.Warning(text);
                });
        }

        /// <summary>
        /// Finalizer to ensure the instance is disposed at some point after the end of its lifecycle.
        /// </summary>
        ~CatapultLibrary() => Dispose();

        /// <summary>
        /// Turns off PCIe, cleans up the access handle and the library.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            LogFunction?.Invoke((uint)(Constants.Log.Info | Constants.Log.Verbose), "Closing down the FPGA...\n");
            PcieEnabled = false;
            NativeLibrary.CloseHandle(Handle);

            NativeLibrary.Dispose();
            LogFunction?.Invoke((uint)(Constants.Log.Info | Constants.Log.Verbose), "Closed down the FPGA...\n");

            _isDisposed = true;
        }


        /// <summary>
        /// Verifies the result of an ICatapultLibrary function call. If the result is SUCCESS or WAIT_TIMEOUT then
        /// nothing happens. Otherwise CatapultFunctionResultException is thrown with the error message form the library.
        /// </summary>
        /// <param name="status">The return value from the library function.</param>
        /// <exception cref="CatapultFunctionResultException">Thrown when the status isn't SUCCESS.</exception>
        public void VerifyResult(Constants.Status status)
        {
            if (status == Constants.Status.Success || status == Constants.Status.WaitTimeout) return;

            var errorMessage = new StringBuilder(512);
            NativeLibrary.GetLastError();
            NativeLibrary.GetLastErrorText(errorMessage, errorMessage.Capacity);

            throw new CatapultFunctionResultException(status, errorMessage.ToString());
        }


        /// <summary>
        /// Uploads the data to the selected slot's input buffer and awaits the output.
        /// But first it checks if there are any other jobs in queue and awaits them if there are any.
        /// </summary>
        /// <param name="inputData">The hardware program's input.</param>
        /// <returns>The resulting output from the FPGA.</returns>
        public async Task<Memory<byte>> ExecuteJob(Memory<byte> inputData)
        {
            // This job will contain the current call
            Task<Memory<byte>> job = null;

            lock (_slotDispatch)
            {
                // go round-robin
                _currentSlot = (_currentSlot + 1) % BufferCount;

                // but try to find the first free slot
                if (!_slotDispatch[_currentSlot].IsCompleted)
                    for (int i = 0; i < BufferCount; i++)
                    {
                        int slot = (_currentSlot + i) % BufferCount;
                        if (_slotDispatch[slot].IsCompleted)
                            _currentSlot = slot;
                    }

                _slotDispatch[_currentSlot] = job = _slotDispatch[_currentSlot]
                    .ContinueWith(_ => { return RunJob(_currentSlot, inputData).Result; });
            }

            return await job;
        }

        /// <summary>
        /// Uploads the data to the selected slot's input buffer and awaits the output.
        /// </summary>
        /// <param name="bufferIndex">The numeric ID of the buffer (called "slot" in the documentation) that the hardware uses for interaction. (ranges from 0 up to BufferCount)</param>
        /// <param name="inputData">The hardware program's input.</param>
        /// <returns>The resulting output from the FPGA.</returns>
        private async Task<Memory<byte>> RunJob(int bufferIndex, Memory<byte> inputData)
        {
            LogFunction?.Invoke((uint)(Constants.Log.Info | Constants.Log.Verbose), $"Job on slot #{bufferIndex} starting...\n");
            Debug.Assert(bufferIndex < BufferCount);
            Debug.Assert(inputData.Length <= BufferSize);
            var slot = (uint)bufferIndex;

            // Make sure the buffer is ready to be written
            bool isInputBufferFull;
            do
            {
                VerifyResult(NativeLibrary.GetInputBufferFull(
                    _handle, slot, out isInputBufferFull));
                if (isInputBufferFull) await Task.Delay(1);
            } while (isInputBufferFull);

            // If the input message is too short, pad it with zeros
            if (inputData.Length < Constants.BufferMessageSizeMinByte)
            {
                var padded = new byte[Constants.BufferMessageSizeMinByte];
                inputData.CopyTo(padded);
                inputData = padded;
            }
            // If the input message isn't 16B aligned, pad it with zeros
            else if(inputData.Length % 16 != 0)
            {
                int paddedLength = (int)Math.Ceiling(inputData.Length / 16.0) * 16;
                var padded = new byte[paddedLength];
                inputData.CopyTo(padded);
                inputData = padded;
            }

            // Acquire I/O buffer addresses
            VerifyResult(NativeLibrary.GetInputBufferPointer(_handle, slot, out IntPtr inputBuffer));
            VerifyResult(NativeLibrary.GetOutputBufferPointer(_handle, slot, out IntPtr outputBuffer));

            LogFunction?.Invoke((uint)Constants.Log.Debug,
                $"Buffer #{slot} @ {inputBuffer.ToInt64()} input start: {Marshal.PtrToStructure<byte>(inputBuffer)}\n");

            // Upload data into input buffer
            unsafe
            {
                inputData.Span.CopyTo(new Span<byte>(inputBuffer.ToPointer(), inputData.Length));
            }
            VerifyResult(NativeLibrary.SendInputBuffer(_handle, slot, (uint)inputData.Length));

            // Wait for the interrupt and download results from output buffer
            var resultSize = await Task.Run(() =>
            {
                VerifyResult(NativeLibrary.WaitOutputBuffer(_handle, slot, out uint bytesReceived));
                return (int)bytesReceived;
            });

            Memory<byte> resultMemory = new byte[resultSize];
            unsafe
            {
                new Span<byte>(outputBuffer.ToPointer(), resultSize).CopyTo(resultMemory.Span);
            }

            LogFunction?.Invoke((uint)Constants.Log.Debug,
                $"Buffer #{slot} @ {outputBuffer.ToInt64()} output start: {Marshal.PtrToStructure<byte>(outputBuffer)}\n");

            // Signal that we are done
            NativeLibrary.DiscardOutputBuffer(_handle, slot);

            LogFunction?.Invoke((uint)(Constants.Log.Info | Constants.Log.Verbose), $"Job on slot #{bufferIndex} finished!\n");
            return resultMemory;
        }
    }
}