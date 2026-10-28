using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Parsley.VisualStudio
{
    /// <summary>
    /// The language server's parsley/methodNames request: the names of the methods synthesized for a grammar's
    /// rules, as the synthesizer names them. The extension asks rather than working them out, since a rule's
    /// methods aren't always named for it alone: foo_bar's are FooBar, and when fooBar comes after it, fooBar's are
    /// FooBar2.
    /// </summary>
    internal static class MethodNameService
    {
        static JsonRpc s_rpc;

        internal static void Attach(JsonRpc rpc) => s_rpc = rpc;

        /// <summary>
        /// The EvaluateXxx method for a rule of a grammar, or null when the server isn't running yet, can't read the
        /// grammar, or the grammar has no such rule
        /// </summary>
        public static async Task<string> GetEvaluateNameAsync(string grammarPath, string rule, CancellationToken ct)
        {
            var rules = await GetAsync(grammarPath, ct).ConfigureAwait(false);
            return null != rules && rules.TryGetValue(rule, out var names) ? names.Evaluate : null;
        }

        /// <summary>
        /// Each rule's methods, or null when the server isn't running yet or can't read the grammar
        /// </summary>
        public static async Task<IReadOnlyDictionary<string, RuleMethodNames>> GetAsync(string grammarPath, CancellationToken ct)
        {
            var rpc = s_rpc;
            if (null == rpc || null == grammarPath) return null;
            JToken reply;
            try
            {
                reply = await rpc.InvokeWithParameterObjectAsync<JToken>("parsley/methodNames",
                    new { textDocument = new { uri = new Uri(grammarPath).AbsoluteUri } }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RemoteInvocationException || ex is ConnectionLostException
                || ex is ObjectDisposedException || ex is OperationCanceledException || ex is UriFormatException)
            {
                return null;
            }
            if (!(reply?["rules"] is JObject rules)) return null;
            var result = new Dictionary<string, RuleMethodNames>(StringComparer.Ordinal);
            foreach (var kv in rules)
            {
                result[kv.Key] = new RuleMethodNames
                {
                    Parse = (string)kv.Value?["parse"],
                    Evaluate = (string)kv.Value?["evaluate"],
                    TryParse = (string)kv.Value?["tryParse"],
                };
            }
            return result;
        }
    }

    /// <summary>
    /// The methods synthesized for one rule. TryParse is null for a rule that isn't shared.
    /// </summary>
    internal sealed class RuleMethodNames
    {
        public string Parse { get; set; }
        public string Evaluate { get; set; }
        public string TryParse { get; set; }
    }
}
