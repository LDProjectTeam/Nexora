using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Vellum.Core.Interfaces;

namespace Vellum.Core.Services
{
    /// <summary>Starts the main launcher (or the self-update replacement) as a child process.</summary>
    public class ProcessStarter : IProcessStarter
    {
        public ProcessStartResult Start(string fileName, IReadOnlyList<string> arguments,
            string workingDirectory, string? standardInput = null)
        {
            try
            {
                if (!File.Exists(fileName))
                    return new ProcessStartResult
                    {
                        Started = false,
                        ErrorMessage = $"The executable does not exist: {fileName}"
                    };

                var info = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                        ? Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory
                        : workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = standardInput != null
                };
                foreach (var argument in arguments)
                    info.ArgumentList.Add(argument);

                var process = Process.Start(info);
                if (process == null)
                    return new ProcessStartResult { Started = false, ErrorMessage = "The process did not start." };

                if (standardInput != null)
                {
                    try
                    {
                        process.StandardInput.WriteLine(standardInput);
                        process.StandardInput.Close();
                    }
                    catch (Exception ex)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        return new ProcessStartResult
                        {
                            Started = false,
                            ErrorMessage = "Could not deliver the launcher handshake: " + ex.Message
                        };
                    }
                }

                return new ProcessStartResult
                {
                    Started = true,
                    ProcessId = process.Id
                };
            }
            catch (Exception ex)
            {
                return new ProcessStartResult { Started = false, ErrorMessage = ex.Message };
            }
        }
    }
}
