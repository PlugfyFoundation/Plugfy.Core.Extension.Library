using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;

using Newtonsoft.Json;

using Plugfy.Core.Commons.Communication;
using Plugfy.Core.Commons.Runtime;

namespace Plugfy.Core.Extension
{
    public class Library : IExtension
    {
        public IReadOnlyCollection<ExecutionOption> ExecutionOptions { get; }

        private readonly IConfiguration _configuration;

        public Library(IConfiguration configuration)
        {
            _configuration = configuration;

            ExecutionOptions = new List<ExecutionOption>
            {
                new ExecutionOption
                {
                    Name = "list",
                    Description = "Lists available assemblies.",
                    Type = typeof(void),
                    IsRequired = false,
                    HelpText = "No parameters required."
                },
                new ExecutionOption
                {
                    Name = "info",
                    Description = "Provides detailed information about an assembly.",
                    Type = typeof(string),
                    IsRequired = true,
                    HelpText = "Provide 'assemblyName'.",
                    Parameters = new List<ExecutionParameter>
                    {
                        new ExecutionParameter
                        {
                            Name = "assemblyName",
                            Description = "Name of the assembly.",
                            Type = typeof(string),
                            IsRequired = true,
                            HelpText = "Assembly file name."
                        }
                    }
                },
                new ExecutionOption
                {
                    Name = "run",
                    Description = "Executes a method in a class.",
                    Type = typeof(object),
                    IsRequired = true,
                    HelpText = "Provide 'assembly', 'class', 'method', and 'parameters'.",
                    Parameters = new List<ExecutionParameter>
                    {
                        new ExecutionParameter
                        {
                            Name = "content",
                            Description = "JSON representation of method execution.",
                            Type = typeof(object),
                            IsRequired = true,
                            HelpText = "JSON with assembly, class, method, and parameters."
                        }
                    }
                }
            };
        }

        public void Execute(ExecutionOption executionOption, dynamic executionParameters, EventHandler eventData)
        {
            if (executionOption == null)
                throw new ArgumentNullException(nameof(executionOption), "Execution option must be provided.");

            // Define o tipo de comunicação com base na configuração
            string communicationType = _configuration["Extensions:Library:Communications:Type"] ?? "STDInOut";

            // Inicializa a comunicação
            IDataCommunication communication = InitializeCommunication(communicationType);

            if (bool.Parse(_configuration["Extensions:Library:Communications:Interactive"] ?? "false"))
            {
                ExecuteInteractive(executionOption, executionParameters, communication, eventData);
            }
            else
            {
                ExecuteNonInteractive(executionOption, executionParameters, communication);
            }
        }              

        private void ExecuteInteractive(ExecutionOption executionOption, dynamic executionParameters, IDataCommunication communication, EventHandler eventData)
        {
            communication.DataReceived += (sender, args) =>
            {
                Console.WriteLine($"[Runner -> Library] Received: {args.Data}");
                eventData?.Invoke(this, new RuntimeEventArgs
                {
                    EventType = "interactive",
                    Message = "Data received from runner",
                    Data = args.Data
                });
            };

            communication.InitializeAsync(executionParameters).Wait();
            communication.StartListeningAsync().Wait();

            string runnerCommand = GetRunnerCommand();
            StartRunner(runnerCommand, communication, executionOption.Name, executionParameters);

            Console.WriteLine("Interactive mode started. Press Enter to terminate...");
            Console.ReadLine();

            communication.CloseAsync().Wait();
        }

        private void ExecuteNonInteractive(ExecutionOption executionOption, dynamic executionParameters, IDataCommunication communication)
        {
            communication.DataReceived += (sender, args) =>
            {
                Console.WriteLine($"[Runner -> Library] Received: {args.Data}");
            };

            communication.InitializeAsync(executionParameters).Wait();
            communication.StartListeningAsync().Wait();

            string runnerCommand = GetRunnerCommand();
            StartRunner(runnerCommand, communication, executionOption.Name, executionParameters);

            communication.CloseAsync().Wait();
        }

        private string GetRunnerCommand()
        {
            var runnerPath = Path.GetFullPath(_configuration["Extensions:Library:Runners:DotNet8:Command"]);
            if (string.IsNullOrEmpty(runnerPath) || !File.Exists(runnerPath))
            {
                throw new FileNotFoundException($"Runner executable not found at path '{runnerPath}'.");
            }

            return runnerPath;
        }

        private void StartRunner(string command, IDataCommunication communication, string commandName, dynamic parameters)
        {
            string serializedParams = JsonConvert.SerializeObject(parameters ?? new { });

            Console.WriteLine($"Starting runner with command: {command} -t {communication.Name} -c {commandName} -p '{serializedParams}'");
            string commandJson = JsonConvert.SerializeObject(new { Type = commandName, Parameters = parameters ?? new { } });

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = command,
                    Arguments = $"-t {communication.Name} -c {commandJson} -p '{serializedParams}'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(command) ?? throw new InvalidOperationException("Invalid runner path.")
                }
            };

            process.OutputDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    Console.WriteLine($"[Runner stdout]: {args.Data}");
            };

            process.ErrorDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    Console.WriteLine($"[Runner stderr]: {args.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();
        }

        private IDataCommunication InitializeCommunication(string communicationType)
        {
            string extensionsPath = Path.GetFullPath(_configuration[$"Extensions:Library:Communications:{communicationType}:LibrariesPath"] ?? "./Extensions/Library/Extensions/Communications/");

            if (!Directory.Exists(extensionsPath))
            {
                throw new DirectoryNotFoundException($"Communication extensions directory '{extensionsPath}' not found.");
            }

            var dllFiles = Directory.GetFiles(extensionsPath, "*.dll");
            foreach (var dll in dllFiles)
            {
                try
                {
                    var assembly = Assembly.LoadFrom(dll);
                    var types = assembly.GetTypes();

                    // Look for a class that implements IDataCommunication matching the type
                    foreach (var type in types)
                    {
                        if (typeof(IDataCommunication).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                        {
                            var instance = (IDataCommunication)Activator.CreateInstance(type);
                            if (instance.Name.Equals(communicationType, StringComparison.OrdinalIgnoreCase))
                            {
                                return instance;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load communication extension from '{dll}': {ex.Message}");
                }
            }

            throw new ArgumentException($"Unsupported communication type: {communicationType}");
        }
    }

    public class RuntimeEventArgs : EventArgs
    {
        public string EventType { get; set; }
        public string Message { get; set; }
        public dynamic Data { get; set; }
    }
}
