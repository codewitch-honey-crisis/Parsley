using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using StreamJsonRpc;

namespace Parsley.VisualStudio
{
    /// <summary>
    /// Starts the XBNF language server (in the extension's server folder) for .xbnf files. It also opens the
    /// custom messages the lookahead glyphs come through, since Visual Studio's language client doesn't show inlay hints.
    /// </summary>
    [ContentType(XbnfContentDefinition.ContentType)]
    [Export(typeof(ILanguageClient))]
    public sealed class XbnfLanguageClient : ILanguageClient, ILanguageClientCustomMessage2
    {
        const string ServerName = "Parsley.LanguageServer";

        public string Name => "Parsley XBNF";
        public IEnumerable<string> ConfigurationSections => null;
        // tells the server this client draws the glyphs itself, so it sends parsley/decisionHintsChanged
        public object InitializationOptions => new { decisionHints = true };
        public IEnumerable<string> FilesToWatch => null;
        public bool ShowNotificationOnInitializeFailed => true;

        public event AsyncEventHandler<EventArgs> StartAsync;
#pragma warning disable CS0067 // part of the interface; the server is stopped by Visual Studio
        public event AsyncEventHandler<EventArgs> StopAsync;
#pragma warning restore CS0067

        public async Task<Connection> ActivateAsync(CancellationToken token)
        {
            await Task.Yield();
            var dir = Path.Combine(Path.GetDirectoryName(typeof(XbnfLanguageClient).Assembly.Location) ?? "", "server");
            // the apphost finds the installed .NET runtime; without it, go through dotnet
            var exe = Path.Combine(dir, ServerName + ".exe");
            var info = File.Exists(exe)
                ? new ProcessStartInfo(exe)
                : new ProcessStartInfo("dotnet", "\"" + Path.Combine(dir, ServerName + ".dll") + "\"");
            info.WorkingDirectory = dir;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;

            var process = new Process { StartInfo = info };
            // the server logs problems to stderr; they go to the debug output rather than filling a pipe
            process.ErrorDataReceived += (s, e) => { if (null != e.Data) Debug.WriteLine("xbnf-lsp: " + e.Data); };
            if (!process.Start()) return null;
            process.BeginErrorReadLine();
            return new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        }

        public async Task OnLoadedAsync()
        {
            if (null != StartAsync) await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }

        public Task OnServerInitializedAsync() => Task.CompletedTask;

        #region Custom messages
        public object MiddleLayer => null;
        public object CustomMessageTarget { get; } = new DecisionHintTarget();
        public Task AttachForCustomMessageAsync(JsonRpc rpc)
        {
            DecisionHintService.Attach(rpc);
            MethodNameService.Attach(rpc);
            return Task.CompletedTask;
        }
        #endregion

        // the older overload, still part of the interface
        public Task OnServerInitializeFailedAsync(Exception e) => Task.CompletedTask;

        public Task<InitializationFailureContext> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        {
            return Task.FromResult(new InitializationFailureContext
            {
                FailureMessage = "The Parsley XBNF language server didn't start. It needs the .NET 8 runtime. "
                    + (initializationState?.InitializationException?.Message ?? ""),
            });
        }
    }
}
