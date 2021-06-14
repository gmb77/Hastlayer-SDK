using Hast.Algorithms;
using Hast.Communication.Exceptions;
using Hast.Layer;
using Hast.Samples.Consumer.SampleRunners;
using Hast.Samples.FSharpSampleAssembly;
using Hast.Samples.SampleAssembly;
using Hast.Transformer.Vhdl.Abstractions.Configuration;
using Lombiq.Arithmetics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

namespace Hast.Samples.Consumer
{
    // In this simple console application we generate hardware from some sample algorithms. Note that the project also
    // references other projects (and the sample assembly as well), so check out those too on hints which Hastlayer
    // projects to reference from your own projects.

    // Configure the whole sample project here or in command line arguments:
    internal static class Configuration
    {
        /// <summary>
        /// Gets or sets which supported hardware device to use? If you leave this empty the first one will be used. If
        /// you're testing Hastlayer locally then you'll need to use the "Nexys A7" or "Nexys4 DDR" devices; for
        /// high-performance local or cloud FPGAs see the docs.
        /// You can also provide this in the -device command line argument.
        /// </summary>
        public static string DeviceName { get; set; } = "Nexys A7";

        /// <summary>
        /// Gets or sets the name of the app. If you're running Hastlayer in the Client flavor, you need to configure
        /// your credentials here.
        /// You can also provide this in the -appname command line argument.
        /// </summary>
        public static string AppName { get; set; } = "appname";

        /// <summary>
        /// Gets or sets the app secret corresponding to of your app. If you're running Hastlayer in the Client flavor,
        /// you need to configure your credentials here.
        /// You can also provide this in the -appsecret command line argument.
        /// </summary>
        public static string AppSecret { get; set; } = "appsecret";

        /// <summary>
        /// Gets or sets which sample algorithm to transform and run. Choose one. Currently the GenomeMatcher sample is
        /// not up-to-date enough and shouldn't be really taken as good example (check out the other ones).
        /// You can also provide this in the -sample command line argument.
        /// </summary>
        public static Sample SampleToRun { get; set; } = Sample.Loopback;

        /// <summary>
        /// Gets or sets the path where the hardware framework is located. The file describing the hardware to be
        /// generated will be saved there as well as anything else necessary. If the path is relative (like the
        /// default) then the file will be saved along this project's executable in the bin output directory.
        /// </summary>
        public static string HardwareFrameworkPath { get; set; } = "HardwareFramework";
    }

    internal static class Program
    {
        private static async Task MainTaskAsync(string[] args)
        {
            /*
            * On a high level these are the steps to use Hastlayer:
            * 1. Create the Hastlayer shell.
            * 2. Configure hardware generation and generate FPGA hardware representation of the given .NET code.
            * 3. Generate proxies for hardware-transformed types and use these proxies to utilize hardware
            *    implementations. (You can see this inside the SampleRunners.)
            */

            // Configuring the Hastlayer shell. Which flavor should we use? If you're unsure then you'll need the
            // Client flavor: This will let you connect to a remote Hastlayer service to run the software to hardware
            // transformation. In most cases the flavor defaults to the one you need.
            //// var hastlayerConfiguration = new HastlayerConfiguration { Flavor = HastlayerFlavor.Client };
            var hastlayerConfiguration = new HastlayerConfiguration();

            // Initializing a Hastlayer shell. Since this is non-trivial to do you can cache this shell object while
            // the program runs and re-use it continuously. No need to always wrap it into a using() like here, just
            // make sure to Dispose() it before the program terminates.
            using var hastlayer = Hastlayer.Create(hastlayerConfiguration);
            // Hooking into an event of Hastlayer so some execution information can be made visible on the
            // console.
            hastlayer.ExecutedOnHardware += (_, e) =>
            {
                var netTime = e.Arguments.HardwareExecutionInformation.HardwareExecutionTimeMilliseconds;
                var grossTime = e.Arguments.HardwareExecutionInformation.FullExecutionTimeMilliseconds;

                Console.WriteLine(
                    $"Executing {e.Arguments.MemberFullName} on hardware took {netTime:0.####} milliseconds (net), " +
                    $"{grossTime:0.####} milliseconds (all together).");

                if (e.Arguments.SoftwareExecutionInformation == null) return;

                // This will be available in case we've set ProxyGenerationConfiguration.VerifyHardwareResults to true,
                // see the notes below, or if the hardware execution was canceled.
                var softwareTime = e.Arguments.SoftwareExecutionInformation.SoftwareExecutionTimeMilliseconds;
                Console.WriteLine($"The verifying software execution took {softwareTime:0.####} milliseconds.");
            };

            // We need to set what kind of device (FPGA/FPGA board) to generate the hardware for.
            var devices = hastlayer.GetSupportedDevices()?.ToList();
            if (devices == null || !devices.Any()) throw new InvalidOperationException("No devices are available!");

            // Let's just use the first one that is available unless it's specified.
            if (string.IsNullOrEmpty(Configuration.DeviceName)) Configuration.DeviceName = devices.First().Name;
            var targetDeviceName = GetArgument(args, "device") ?? Configuration.DeviceName;
            var selectedDevice = devices.FirstOrDefault(device => device.Name == targetDeviceName);
            if (selectedDevice == null) throw new InvalidOperationException($"Target device '{targetDeviceName}' not found!");

            var configuration = new HardwareGenerationConfiguration(selectedDevice.Name, Configuration.HardwareFrameworkPath);
            var proxyConfiguration = new ProxyGenerationConfiguration();
            if (args.Contains("-verify")) proxyConfiguration.VerifyHardwareResults = true;

            // If you're running Hastlayer in the Client flavor, you also need to configure some credentials:
            var remoteClientConfiguration = configuration.RemoteClientConfiguration();
            remoteClientConfiguration.AppName = GetArgument(args, "appname") ?? Configuration.AppName;
            remoteClientConfiguration.AppSecret = GetArgument(args, "appsecret") ?? Configuration.AppSecret;
            if (hastlayerConfiguration.Flavor == HastlayerFlavor.Client &&
                remoteClientConfiguration.AppSecret == "appsecret")
            {
                throw new InvalidOperationException(
                    "You haven't changed the default remote credentials! Register on hastlayer.com to receive access if you don't have it yet.");
            }

            // If the sample was selected in the command line use that, or otherwise the default.
            Configuration.SampleToRun = (Sample)Enum.Parse(
                typeof(Sample),
                GetArgument(args, "sample") ?? Configuration.SampleToRun.ToString(),
                ignoreCase: true);

            // Letting the configuration of samples run. Check out those methods too!
            var sampleRunner = ConfigureSamples(configuration, args);

            Console.WriteLine("Hardware generation starts.");

            // Generating hardware from the sample assembly with the given configuration. Be sure to use Debug
            // assemblies!
            var hardwareRepresentation = await hastlayer.GenerateHardwareAsync(
                new[]
                {
                    // Selecting any type from the sample assembly here just to get its Assembly object.
                    typeof(PrimeCalculator).Assembly,
                    typeof(Fix64).Assembly,
                    typeof(FSharpParallelAlgorithmContainer).Assembly,
                    // Note that the assemblies used by code to be transformed also need to be added
                    // separately. E.g. Posit is used by Hast.Samples.SampleAssembly which in turn also uses
                    // ImmutableArray.
                    typeof(Posit).Assembly,
                    typeof(ImmutableArray).Assembly,
                },
                configuration);

            Console.WriteLine("Hardware generation finished.\n");

            // Be sure to check out transformation warnings. Most of the time the issues noticed shouldn't cause
            // any problems, but sometimes they can.
            if (hardwareRepresentation.HardwareDescription.Warnings.Any())
            {
                Console.WriteLine("There were transformation warnings in the logs, which may hint on issues that can " +
                                  "cause the hardware implementation to produce incorrect results.\n");
            }

            // With the "-build" argument you won't attempt to run the app. Handy if you are just preparing binaries for
            // a different host.
            if (GetArgument(args, "build") != null) return;

            Console.WriteLine("Starting hardware execution.");
            await RunSamplesAsync(sampleRunner, hastlayer, hardwareRepresentation, proxyConfiguration);
        }

        private static ISampleRunner ConfigureSamples(HardwareGenerationConfiguration configuration, IList<string> args)
        {
            ISampleRunner sampleRunner = Configuration.SampleToRun switch
            {
                Sample.Fix64Calculator => new Fix64CalculatorSampleRunner(),
                Sample.FSharpParallelAlgorithm => new FSharpParallelAlgorithmSampleRunner(),
                Sample.GenomeMatcher => new GenomeMatcherSampleRunner(),
                Sample.ParallelAlgorithm => new ParallelAlgorithmSampleRunner(),
                Sample.ImageProcessingAlgorithms => new ImageProcessingAlgorithmsSampleRunner(),
                Sample.Loopback => new LoopbackSampleRunner(),
                Sample.MemoryTest => new MemoryTestSampleRunner(),
                Sample.MonteCarloPiEstimator => new MonteCarloPiEstimatorSampleRunner(),
                Sample.ObjectOrientedShowcase => new ObjectOrientedShowcaseSampleRunner(),
                Sample.PositCalculator => new PositCalculatorSampleRunner(),
                Sample.Posit32AdvancedCalculator => new Posit32AdvancedCalculatorSampleRunner(),
                Sample.Posit32Calculator => new Posit32CalculatorSampleRunner(),
                Sample.Posit32FusedCalculator => new Posit32FusedCalculatorSampleRunner(),
                Sample.PrimeCalculator => new PrimeCalculatorSampleRunner(),
                Sample.RecursiveAlgorithms => new RecursiveAlgorithmsSampleRunner(),
                Sample.SimdCalculator => new SimdCalculatorSampleRunner(),
                Sample.UnumCalculator => new UnumCalculatorSampleRunner(),
                _ => throw new ArgumentException($"Unknown sample '{Configuration.SampleToRun}'."),
            };
            sampleRunner.Configure(configuration);
            configuration.Label = GetArgument(args, "name") ?? Configuration.SampleToRun.ToString();

            if (GetArgument(args, "replace") is { } replacement)
            {
                var parts = replacement.Split('=', 2);
                configuration.GetOrAddReplacements()[parts[0]] = parts[1];
            }

            // The generated VHDL code will contain debug-level information, though it will be slower to create.
            configuration.VhdlTransformerConfiguration().VhdlGenerationConfiguration = VhdlGenerationConfiguration.Debug;

            return sampleRunner;
        }

        private static async Task RunSamplesAsync(
            ISampleRunner sampleRunner,
            IHastlayer hastlayer,
            IHardwareRepresentation hardwareRepresentation,
            IProxyGenerationConfiguration proxyConfiguration)
        {
            try
            {
                await sampleRunner.RunAsync(hastlayer, hardwareRepresentation, proxyConfiguration);
            }
            catch (AggregateException ex) when (ex.InnerException is HardwareExecutionResultMismatchException exception)
            {
                // If you set ProxyGenerationConfiguration.VerifyHardwareResults to true (when calling
                // GenerateProxy()) then everything will be computed in software as well to check the hardware.
                // You'll get such an exception if there is any mismatch. This shouldn't normally happen, but it's
                // not impossible in corner cases.
                var mismatches = exception
                    .Mismatches?
                    .ToList() ?? new List<HardwareExecutionResultMismatchException.Mismatch>();
                var mismatchCount = mismatches.Count;
                Console.WriteLine(
                    $"There {(mismatchCount == 1 ? "was a mismatch" : $"were {mismatchCount} mismatches")} between " +
                    $"the software and hardware execution's results! Mismatch{(mismatchCount == 1 ? string.Empty : "es")}:");

                foreach (var mismatch in mismatches)
                {
                    Console.WriteLine("* " + mismatch);
                }
            }
        }

        private static string GetArgument(IList<string> argsList, string name)
        {
            name = "-" + name;
            int index = argsList.IndexOf(name) + 1;
            if (index <= 0) return null;
            if (index == argsList.Count) index--; // if it's the last element just return the switch.
            return argsList[index];
        }

        private static async Task Main(string[] args)
        {
            // Wrapping the whole program into a try-catch here so it's a bit more convenient above.
            try
            {
                await MainTaskAsync(args);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(ex.ToString());
            }
        }
    }
}
