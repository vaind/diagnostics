// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Internal.Common.Utils;
using Microsoft.Tools.Common;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Binding;
using System.CommandLine.Rendering;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Stacks;
using Dia2Lib;
using System.Text.Json;
using Sentry;

namespace Microsoft.Diagnostics.Tools.Trace
{
    internal static class CollectCommandHandler
    {
        internal static bool IsQuiet
        {get; set; }
        static void ConsoleWriteLine(string str)
        {
            if (!IsQuiet)
            {
                Console.Out.WriteLine(str);
            }
        }
        delegate Task<int> CollectDelegate(CancellationToken ct, IConsole console, int processId, FileInfo output, uint buffersize, string providers, string profile, TraceFileFormat format, TimeSpan duration, string clrevents, string clreventlevel, string name, string port, bool showchildio, bool resumeRuntime);
        public static long FindPrimeNumber(int n)
        {
            int count = 0;
            long a = 2;
            while (count < n)
            {
                long b = 2;
                int prime = 1;// to check if found a prime
                while (b * b <= a)
                {
                    if (a % b == 0)
                    {
                        prime = 0;
                        break;
                    }
                    b++;
                }
                if (prime > 0)
                {
                    count++;
                }
                a++;
            }
            return (--a);
        }

        /// <summary>
        /// Collects a diagnostic trace from a currently running process or launch a child process and trace it.
        /// Append -- to the collect command to instruct the tool to run a command and trace it immediately. By default the IO from this process is hidden, but the --show-child-io option may be used to show the child process IO.
        /// </summary>
        /// <param name="ct">The cancellation token</param>
        /// <param name="console"></param>
        /// <param name="processId">The process to collect the trace from.</param>
        /// <param name="name">The name of process to collect the trace from.</param>
        /// <param name="output">The output path for the collected trace data.</param>
        /// <param name="buffersize">Sets the size of the in-memory circular buffer in megabytes.</param>
        /// <param name="providers">A list of EventPipe providers to be enabled. This is in the form 'Provider[,Provider]', where Provider is in the form: 'KnownProviderName[:Flags[:Level][:KeyValueArgs]]', and KeyValueArgs is in the form: '[key1=value1][;key2=value2]'</param>
        /// <param name="profile">A named pre-defined set of provider configurations that allows common tracing scenarios to be specified succinctly.</param>
        /// <param name="format">The desired format of the created trace file.</param>
        /// <param name="duration">The duration of trace to be taken. </param>
        /// <param name="clrevents">A list of CLR events to be emitted.</param>
        /// <param name="clreventlevel">The verbosity level of CLR events</param>
        /// <param name="diagnosticPort">Path to the diagnostic port to be used.</param>
        /// <param name="showchildio">Should IO from a child process be hidden.</param>
        /// <param name="resumeRuntime">Resume runtime once session has been initialized.</param>
        /// <returns></returns>
        private static async Task<int> Collect(CancellationToken ct, IConsole console, int processId, FileInfo output, uint buffersize, string providers, string profile, TraceFileFormat format, TimeSpan duration, string clrevents, string clreventlevel, string name, string diagnosticPort, bool showchildio, bool resumeRuntime)
        {
            bool collectionStopped = false;
            bool cancelOnEnter = true;
            bool cancelOnCtrlC = true;
            bool printStatusOverTime = true;
            int ret = ReturnCode.Ok;
            IsQuiet = showchildio;

            try
            {
                Debug.Assert(output != null);
                Debug.Assert(profile != null);

                if (ProcessLauncher.Launcher.HasChildProc && showchildio)
                {
                    // If showing IO, then all IO (including CtrlC) behavior is delegated to the child process
                    cancelOnCtrlC = false;
                    cancelOnEnter = false;
                    printStatusOverTime = false;
                }
                else
                {
                    cancelOnCtrlC = true;
                    cancelOnEnter = !Console.IsInputRedirected;
                    printStatusOverTime = !Console.IsOutputRedirected;
                }

                if (!cancelOnCtrlC)
                {
                    ct = CancellationToken.None;
                }




                processId = Process.GetCurrentProcess().Id;

                if (profile.Length == 0 && providers.Length == 0 && clrevents.Length == 0)
                {
                    ConsoleWriteLine("No profile or providers specified, defaulting to trace profile 'cpu-sampling'");
                    profile = "cpu-sampling";
                }

                Dictionary<string, string> enabledBy = new Dictionary<string, string>();

                var providerCollection = Extensions.ToProviders(providers);
                foreach (EventPipeProvider providerCollectionProvider in providerCollection)
                {
                    enabledBy[providerCollectionProvider.Name] = "--providers ";
                }

                if (profile.Length != 0)
                {
                    var selectedProfile = ListProfilesCommandHandler.DotNETRuntimeProfiles
                        .FirstOrDefault(p => p.Name.Equals(profile, StringComparison.OrdinalIgnoreCase));
                    if (selectedProfile == null)
                    {
                        Console.Error.WriteLine($"Invalid profile name: {profile}");
                        return ReturnCode.ArgumentError;
                    }

                    Profile.MergeProfileAndProviders(selectedProfile, providerCollection, enabledBy);
                }

                // Parse --clrevents parameter
                if (clrevents.Length != 0)
                {
                    // Ignore --clrevents if CLR event provider was already specified via --profile or --providers command.
                    if (enabledBy.ContainsKey(Extensions.CLREventProviderName))
                    {
                        ConsoleWriteLine($"The argument --clrevents {clrevents} will be ignored because the CLR provider was configured via either --profile or --providers command.");
                    }
                    else
                    {
                        var clrProvider = Extensions.ToCLREventPipeProvider(clrevents, clreventlevel);
                        providerCollection.Add(clrProvider);
                        enabledBy[Extensions.CLREventProviderName] = "--clrevents";
                    }
                }


                if (providerCollection.Count <= 0)
                {
                    Console.Error.WriteLine("No providers were specified to start a trace.");
                    return ReturnCode.ArgumentError;
                }

                PrintProviders(providerCollection, enabledBy);


                var sw = Stopwatch.StartNew();

                DiagnosticsClient diagnosticsClient;
                Process process = null;
                DiagnosticsClientBuilder builder = new DiagnosticsClientBuilder("dotnet-trace", 10);
                var shouldExit = new ManualResetEvent(false);
                ct.Register(() => shouldExit.Set());
                using (DiagnosticsClientHolder holder = await builder.Build(ct, processId, diagnosticPort, showChildIO: showchildio, printLaunchCommand: true))
                {
                    string processMainModuleFileName = $"Process{processId}";

                    // if builder returned null, it means we received ctrl+C while waiting for clients to connect. Exit gracefully.
                    if (holder == null)
                    {
                        return await Task.FromResult(ReturnCode.Ok);
                    }
                    diagnosticsClient = holder.Client;
                    if (ProcessLauncher.Launcher.HasChildProc)
                    {
                        process = Process.GetProcessById(holder.EndpointInfo.ProcessId);
                    }
                    else if (IpcEndpointConfig.TryParse(diagnosticPort, out IpcEndpointConfig portConfig) && (portConfig.IsConnectConfig || portConfig.IsListenConfig))
                    {
                        // No information regarding process (could even be a routed process),
                        // use "file" part of IPC channel name as process main module file name.
                        processMainModuleFileName = Path.GetFileName(portConfig.Address);
                    }
                    else
                    {
                        process = Process.GetProcessById(processId);
                    }

                    if (process != null)
                    {
                        // Reading the process MainModule filename can fail if the target process closes
                        // or isn't fully setup. Retry a few times to attempt to address the issue
                        for (int attempts = 0; attempts < 10; attempts++)
                        {
                            try
                            {
                                processMainModuleFileName = process.MainModule.FileName;
                                break;
                            }

                            catch
                            {
                                Thread.Sleep(200);
                            }
                        }

                    }

                    if (String.Equals(output.Name, DefaultTraceName, StringComparison.OrdinalIgnoreCase))
                    {
                        DateTime now = DateTime.Now;
                        var processMainModuleFileInfo = new FileInfo(processMainModuleFileName);
                        output = new FileInfo($"{processMainModuleFileInfo.Name}_{now:yyyyMMdd}_{now:HHmmss}.nettrace");
                    }

                    var shouldStopAfterDuration = duration != default(TimeSpan);
                    var rundownRequested = false;
                    System.Timers.Timer durationTimer = null;

                    using (SentrySdk.Init(o =>
                    {
                        // Tells which project in Sentry to send events to:
                        o.Dsn = "https://eb18e953812b41c3aeb042e666fd3b5c@o447951.ingest.sentry.io/5428537";
                        // When configuring for the first time, to see what the SDK is doing:
                        o.Debug = true;
                        // Set traces_sample_rate to 1.0 to capture 100% of transactions for performance monitoring.
                        // We recommend adjusting this value in production.
                        o.TracesSampleRate = 1.0;
                        // Enable Global Mode if running in a client app
                        o.IsGlobalModeEnabled = true;
                    }))
                    using (VirtualTerminalMode vTermMode = printStatusOverTime ? VirtualTerminalMode.TryEnable() : null)
                    {
                        var tx = SentrySdk.StartTransaction("dotnet-trace", "profiling");
                        
                        EventPipeSession session = null;
                        try
                        {
                            session = diagnosticsClient.StartEventPipeSession(providerCollection, true, (int)buffersize);
                            if (resumeRuntime)
                            {
                                try
                                {
                                    diagnosticsClient.ResumeRuntime();
                                }
                                catch (UnsupportedCommandException)
                                {
                                    // Noop if command is unsupported, since the target is most likely a 3.1 app.
                                }
                            }
                        }
                        catch (DiagnosticsClientException e)
                        {
                            Console.Error.WriteLine($"Unable to start a tracing session: {e.ToString()}");
                            return ReturnCode.SessionCreationError;
                        }
                        catch (UnauthorizedAccessException e)
                        {
                            Console.Error.WriteLine($"dotnet-trace does not have permission to access the specified app: {e.GetType()}");
                            return ReturnCode.SessionCreationError;
                        }

                        if (session == null)
                        {
                            Console.Error.WriteLine("Unable to create session.");
                            return ReturnCode.SessionCreationError;
                        }

                        if (shouldStopAfterDuration)
                        {
                            durationTimer = new System.Timers.Timer(duration.TotalMilliseconds);
                            durationTimer.Elapsed += (s, e) => shouldExit.Set();
                            durationTimer.AutoReset = false;
                        }

                        var stopwatch = new Stopwatch();
                        durationTimer?.Start();
                        stopwatch.Start();

                        LineRewriter rewriter = null;

                        // Running the profile data collection on a custom thread so we can skip 
                        //var profilerThread = new Thread(new ThreadStart(() => {
                        //    var source = new EventPipeEventSource(session.EventStream);

                        //    var profiler = new SentrySampleProfiler(source);

                        //    //source.Dynamic.All += (TraceEvent obj) => {
                        //    //    //Console.WriteLine(obj.EventName);
                        //    //};
                        //    try {
                        //        source.Process();
                        //        Console.WriteLine("Profiling finished");
                        //    }
                        //    // NOTE: This exception does not currently exist. It is something that needs to be added to TraceEvent.
                        //    catch (Exception e) {
                        //        Console.WriteLine("Error encountered while processing events");
                        //        Console.WriteLine(e.ToString());
                        //    }
                        //})) {
                        //    Name = "sentry-profiler"
                        //};
                        //profilerThread.Start();

                        using (var fs = new FileStream(output.FullName, FileMode.Create, FileAccess.Write))
                        {
                            ConsoleWriteLine($"Process        : {processMainModuleFileName}");
                            ConsoleWriteLine($"Output File    : {fs.Name}");
                            if (shouldStopAfterDuration)
                                ConsoleWriteLine($"Trace Duration : {duration.ToString(@"dd\:hh\:mm\:ss")}");
                            ConsoleWriteLine("\n\n");

                                var fileInfo = new FileInfo(output.FullName);
                            Task copyTask = session.EventStream.CopyToAsync(fs);
                            Task shouldExitTask = copyTask.ContinueWith((task) => shouldExit.Set());

                            if (printStatusOverTime)
                            {
                                rewriter = new LineRewriter { LineToClear = Console.CursorTop - 1 };
                                Console.CursorVisible = false;
                            }

                            Action printStatus = () =>
                            {
                                if (printStatusOverTime)
                                {
                                    rewriter?.RewriteConsoleLine();
                                    fileInfo.Refresh();
                                    ConsoleWriteLine($"[{stopwatch.Elapsed.ToString(@"dd\:hh\:mm\:ss")}]\tRecording trace {GetSize(fileInfo.Length)}");
                                    ConsoleWriteLine("Press <Enter> or <Ctrl+C> to exit...");
                                }

                                if (rundownRequested)
                                    ConsoleWriteLine("Stopping the trace. This may take several minutes depending on the application being traced.");

                                FindPrimeNumber(100000);
                            };

                            while (!shouldExit.WaitOne(100) && !(cancelOnEnter && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter))
                                printStatus();

                            // if the CopyToAsync ended early(target program exited, etc.), then we don't need to stop the session.
                            if (!copyTask.Wait(0)) {
                                // Behavior concerning Enter moving text in the terminal buffer when at the bottom of the buffer
                                // is different between Console/Terminals on Windows and Mac/Linux
                                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                                    printStatusOverTime &&
                                    rewriter != null &&
                                    Math.Abs(Console.CursorTop - Console.BufferHeight) == 1) {
                                    rewriter.LineToClear--;
                                }
                                collectionStopped = true;
                                durationTimer?.Stop();
                                rundownRequested = true;
                                session.Stop();

                                do {
                                    printStatus();
                                } while (!copyTask.Wait(100));
                            }
                            // At this point the copyTask will have finished, so wait on the shouldExitTask in case it threw
                            // an exception or had some other interesting behavior
                            shouldExitTask.Wait();
                        }

                        ConsoleWriteLine($"\nTrace completed.");

                        ConsoleWriteLine($"\nSentry TX trace ID: ${tx.TraceId}");
                        ConsoleWriteLine($"\nSentry TX: ${tx}");
                        tx.Finish();

                        ConvertToSentry(output.FullName, tx);
                    }

                    if (!collectionStopped && !ct.IsCancellationRequested)
                    {
                        // If the process is shutting down by itself print the return code from the process.
                        // Capture this before leaving the using, as the Dispose of the DiagnosticsClientHolder
                        // may terminate the target process causing it to have the wrong error code
                        if (ProcessLauncher.Launcher.HasChildProc && ProcessLauncher.Launcher.ChildProc.WaitForExit(5000))
                        {
                            ret = ProcessLauncher.Launcher.ChildProc.ExitCode;
                            ConsoleWriteLine($"Process exited with code '{ret}'.");
                            collectionStopped = true;
                        }
                    }
                }
            }
            catch (CommandLineErrorException e)
            {
                Console.Error.WriteLine($"[ERROR] {e.Message}");
                collectionStopped = true;
                ret = ReturnCode.TracingError;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex.ToString()}");
                collectionStopped = true;
                ret = ReturnCode.TracingError;
            }
            finally
            {
                if (printStatusOverTime)
                {
                    if (console.GetTerminal() != null)
                        Console.CursorVisible = true;
                }
            
                if (ProcessLauncher.Launcher.HasChildProc)
                {
                    if (!collectionStopped || ct.IsCancellationRequested)
                    {
                        ret = ReturnCode.TracingError;
                    }
                }
                ProcessLauncher.Launcher.Cleanup();
            }
            return await Task.FromResult(ret);
        }

        private static void ConvertToSentry(string fileToConvert, ITransaction sentryTx) {
            var outputFilename = Path.ChangeExtension(fileToConvert, ".json");
            Console.Out.WriteLine($"Writing:\t{outputFilename}");

            // We convert the EventPipe log (ETL) to ETLX to get processed stack traces. 
            // See https://github.com/microsoft/perfview/blob/main/documentation/TraceEvent/TraceEventProgrammersGuide.md#using-call-stacks-with-the-traceevent-library
            // NOTE: we may be able to skip collecting to the original ETL (nettrace) and just create ETLX directly, see CreateFromEventPipeDataFile() code.
            // ContinueOnError - best-effort if there's a broken trace. The resulting file may contain broken stacks as a result.
            var etlxFilePath = TraceLog.CreateFromEventPipeDataFile(fileToConvert, null, new TraceLogOptions() { ContinueOnError = true });

            using (var eventLog = new TraceLog(etlxFilePath)) {
                var processor = new SentrySampleProfiler(eventLog);
                var profile = processor.Process();

                if (File.Exists(outputFilename)) {
                    File.Delete(outputFilename);
                }

                using (FileStream f = new FileStream(outputFilename, FileMode.OpenOrCreate, FileAccess.ReadWrite)) {
                    var options = new JsonWriterOptions() { SkipValidation = true, Indented = false };
                    using var writer = new Utf8JsonWriter(f, options);
                    writer.WriteStartObject();
                    writer.WriteRawValue("""
                          "debug_meta": {
                            "images": []
                          },
                          "device": {
                            "architecture": "x86_64"
                          },
                          "environment": "development"
                    """, skipInputValidation: true);
                    writer.WriteRawValue($"\"event_id\": \"{SentryId.Create()}\"", skipInputValidation: true);
                    writer.WriteRawValue("""
                          "measurements": {},
                          "os": {
                            "build": "19045",
                            "name": "Windows",
                            "build_number": "10.0.19045"
                          },
                          "platform": "dotnet"
                    """, skipInputValidation: true);
                    writer.WriteRawValue($"\"timestamp\": \"${sentryTx.StartTimestamp}\"", skipInputValidation: true);
                    writer.WriteRawValue("""
                          "transaction": {
                            "active_thread_id": "259"
                    """, skipInputValidation: true);

                    writer.WriteRawValue($"\"id\": \"${sentryTx.SpanId}\"", skipInputValidation: true);
                    writer.WriteRawValue($"\"trace_id\": \"{sentryTx.TraceId}\"", skipInputValidation: true);
                    writer.WriteRawValue("""
                            "name": "dotnet-trace"
                          },
                          "version": "1"
                    """, skipInputValidation: true);
                    writer.WritePropertyName("profile");
                    profile.WriteTo(writer, null);
                    writer.WriteEndObject();
                    writer.Flush();
                }
            }

            if (File.Exists(etlxFilePath)) {
                File.Delete(etlxFilePath);
            }

            Console.Out.WriteLine("Conversion complete");
        }

        private static void PrintProviders(IReadOnlyList<EventPipeProvider> providers, Dictionary<string, string> enabledBy)
        {
            ConsoleWriteLine("");
            ConsoleWriteLine(String.Format("{0, -40}","Provider Name") + String.Format("{0, -20}","Keywords") +
                String.Format("{0, -20}","Level") + "Enabled By");  // +4 is for the tab
            foreach (var provider in providers)
            {
                ConsoleWriteLine(String.Format("{0, -80}", $"{GetProviderDisplayString(provider)}") + $"{enabledBy[provider.Name]}");
            }
            ConsoleWriteLine("");
        }
        private static string GetProviderDisplayString(EventPipeProvider provider) =>
            String.Format("{0, -40}", provider.Name) + String.Format("0x{0, -18}", $"{provider.Keywords:X16}") + String.Format("{0, -8}", provider.EventLevel.ToString() + $"({(int)provider.EventLevel})");

        private static string GetSize(long length)
        {
            if (length > 1e9)
                return String.Format("{0,-8} (GB)", $"{length / 1e9:0.00##}");
            else if (length > 1e6)
                return String.Format("{0,-8} (MB)", $"{length / 1e6:0.00##}");
            else if (length > 1e3)
                return String.Format("{0,-8} (KB)", $"{length / 1e3:0.00##}");
            else
                return String.Format("{0,-8} (B)", $"{length / 1.0:0.00##}");
        }

        public static Command CollectCommand() =>
            new Command(
                name: "collect",
                description: "Collects a diagnostic trace from a currently running process or launch a child process and trace it. Append -- to the collect command to instruct the tool to run a command and trace it immediately. When tracing a child process, the exit code of dotnet-trace shall be that of the traced process unless the trace process encounters an error.") 
            {
                // Handler
                HandlerDescriptor.FromDelegate((CollectDelegate)Collect).GetCommandHandler(),
                // Options
                CommonOptions.ProcessIdOption(),
                CircularBufferOption(),
                OutputPathOption(),
                ProvidersOption(),
                ProfileOption(),
                CommonOptions.FormatOption(),
                DurationOption(),
                CLREventsOption(),
                CLREventLevelOption(),
                CommonOptions.NameOption(),
                DiagnosticPortOption(),
                ShowChildIOOption(),
                ResumeRuntimeOption()
            };

        private static uint DefaultCircularBufferSizeInMB() => 256;

        private static Option CircularBufferOption() =>
            new Option(
                alias: "--buffersize",
                description: $"Sets the size of the in-memory circular buffer in megabytes. Default {DefaultCircularBufferSizeInMB()} MB.")
            {
                Argument = new Argument<uint>(name: "size", getDefaultValue: DefaultCircularBufferSizeInMB)
            };

        public static string DefaultTraceName => "default";

        private static Option OutputPathOption() =>
            new Option(
                aliases: new[] { "-o", "--output" },
                description: $"The output path for the collected trace data. If not specified it defaults to '<appname>_<yyyyMMdd>_<HHmmss>.nettrace', e.g., 'myapp_20210315_111514.nettrace'.")
            {
                Argument = new Argument<FileInfo>(name: "trace-file-path", getDefaultValue: () => new FileInfo(DefaultTraceName))
            };

        private static Option ProvidersOption() =>
            new Option(
                alias: "--providers",
                description: @"A comma delimitted list of EventPipe providers to be enabled. This is in the form 'Provider[,Provider]'," +
                             @"where Provider is in the form: 'KnownProviderName[:[Flags][:[Level][:[KeyValueArgs]]]]', and KeyValueArgs is in the form: " +
                             @"'[key1=value1][;key2=value2]'.  Values in KeyValueArgs that contain ';' or '=' characters need to be surrounded by '""', " +
                             @"e.g., FilterAndPayloadSpecs=""MyProvider/MyEvent:-Prop1=Prop1;Prop2=Prop2.A.B;"".  Depending on your shell, you may need to " +
                             @"escape the '""' characters and/or surround the entire provider specification in quotes, e.g., " +
                             @"--providers 'KnownProviderName:0x1:1:FilterSpec=\""KnownProviderName/EventName:-Prop1=Prop1;Prop2=Prop2.A.B;\""'. These providers are in " +
                             @"addition to any providers implied by the --profile argument. If there is any discrepancy for a particular provider, the " +
                             @"configuration here takes precedence over the implicit configuration from the profile.  See documentation for examples.")
            {
                Argument = new Argument<string>(name: "list-of-comma-separated-providers", getDefaultValue: () => string.Empty) // TODO: Can we specify an actual type?
            };

        private static Option ProfileOption() =>
            new Option(
                alias: "--profile",
                description: @"A named pre-defined set of provider configurations that allows common tracing scenarios to be specified succinctly.")
            {
                Argument = new Argument<string>(name: "profile-name", getDefaultValue: () => string.Empty)
            };

        private static Option DurationOption() =>
            new Option(
                alias: "--duration",
                description: @"When specified, will trace for the given timespan and then automatically stop the trace. Provided in the form of dd:hh:mm:ss.")
            {
                Argument = new Argument<TimeSpan>(name: "duration-timespan", getDefaultValue: () => default)
            };
        
        private static Option CLREventsOption() => 
            new Option(
                alias: "--clrevents",
                description: @"List of CLR runtime events to emit.")
            {
                Argument = new Argument<string>(name: "clrevents", getDefaultValue: () => string.Empty)
            };

        private static Option CLREventLevelOption() => 
            new Option(
                alias: "--clreventlevel",
                description: @"Verbosity of CLR events to be emitted.")
            {
                Argument = new Argument<string>(name: "clreventlevel", getDefaultValue: () => string.Empty)
            };
        private static Option DiagnosticPortOption() =>
            new Option(
                alias: "--diagnostic-port",
                description: @"The path to a diagnostic port to be used.")
            {
                Argument = new Argument<string>(name: "diagnosticPort", getDefaultValue: () => string.Empty)
            };
        private static Option ShowChildIOOption() =>
            new Option(
                alias: "--show-child-io",
                description: @"Shows the input and output streams of a launched child process in the current console.")
            {
                Argument = new Argument<bool>(name: "show-child-io", getDefaultValue: () => false)
            };

        private static Option ResumeRuntimeOption() =>
            new Option(
                alias: "--resume-runtime",
                description: @"Resume runtime once session has been initialized, defaults to true. Disable resume of runtime using --resume-runtime:false")
            {
                Argument = new Argument<bool>(name: "resumeRuntime", getDefaultValue: () => true)
            };
    }
}
