#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP server â€” Local Code Mode Sandbox Engine.
//
// Zero-dependency, pure-C# sandboxed JavaScript-subset evaluator designed for
// MCP Code Mode orchestration (inspired by Cloudflare Code Mode, running 100%
// locally inside Scio.Server with zero network, zero filesystem, zero .NET
// reflection, and hard step/call/time/output budgets).
//
// Supported language surface:
//   - let / const / var, assignments (=, +=, -=, *=, /=, ++, --)
//   - primitives (null, undefined, bool, number, string, template literals)
//   - arrays [...], objects { a: 1, b }, member access (. / ?. / [] / ?.[])
//   - operators (+, -, *, /, %, ==, ===, !=, !==, <, <=, >, >=, &&, ||, ??, !, ?:, typeof, in)
//   - if / else, for (let x of arr), for (let k in obj), for (init; cond; step),
//     while, break, continue, return, try / catch, throw
//   - function declarations & arrow functions (closures)
//   - safe built-ins: Array/String methods, Object, Math, JSON, Number/String/Boolean,
//     round(), console.log(), and host-injected `scio` / `call` / `search` / `describe`.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scio.Server
{
    internal sealed class CodeModeException : Exception
    {
        public string Code { get; }
        public CodeModeException(string code, string message) : base(message) { Code = code; }
    }

    internal sealed class CodeModeLimits
    {
        public int MaxSteps { get; set; } = 50_000;
        public int TimeoutMs { get; set; } = 30_000;
        public int MaxCallDepth { get; set; } = 32;
        public int MaxLogs { get; set; } = 50;
    }

    internal sealed class CodeModeRunOutput
    {
        public JToken Result { get; set; }
        public List<string> Logs { get; } = new List<string>();
        public int StepsExecuted { get; set; }
    }

    internal static class CodeModeEngine
    {
        // Marker for undefined (distinct from null when needed, serializes to null in JSON).
        internal sealed class UndefinedValue
        {
            public static readonly UndefinedValue Instance = new UndefinedValue();
            private UndefinedValue() { }
            public override string ToString() => "undefined";
        }

        internal delegate object HostCallable(List<object> args);

        internal sealed class DynamicHostObject
        {
            private readonly Dictionary<string, object> _props = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly Func<string, object> _fallback;

            public DynamicHostObject(Func<string, object> fallback = null) { _fallback = fallback; }

            public object this[string key]
            {
                get => _props.TryGetValue(key, out object v) ? v : (_fallback != null ? _fallback(key) : UndefinedValue.Instance);
                set => _props[key] = value;
            }

            public bool TryGet(string key, out object value)
            {
                if (_props.TryGetValue(key, out value)) return true;
                if (_fallback != null)
                {
                    value = _fallback(key);
                    return value != UndefinedValue.Instance;
                }
                value = UndefinedValue.Instance;
                return false;
            }
        }

        public static CodeModeRunOutput Execute(
            string code,
            Dictionary<string, object> globals,
            CodeModeLimits limits,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new CodeModeException("empty_code", "'code' must be a non-empty script.");

            limits = limits ?? new CodeModeLimits();
            List<Token> tokens = Lexer.Tokenize(code);
            List<Stmt> program = new Parser(tokens).ParseProgram();

            var output = new CodeModeRunOutput();
            var env = new Env(null);

            // Safe built-ins
            RegisterBuiltins(env, output, limits);
            if (globals != null)
            {
                foreach (var kv in globals)
                    env.Define(kv.Key, kv.Value);
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, limits.TimeoutMs));
            var eval = new Evaluator(limits, deadline, ct, output);
            try
            {
                object last = null;
                foreach (Stmt stmt in program)
                {
                    var res = eval.ExecStmt(stmt, env);
                    if (res.Kind == FlowKind.Return)
                    {
                        last = res.Value;
                        break;
                    }
                    if (res.Kind == FlowKind.Normal && res.HasValue)
                        last = res.Value;
                }
                output.Result = ToJToken(last);
                output.StepsExecuted = eval.Steps;
                return output;
            }
            catch (ScriptThrowException ste)
            {
                throw new CodeModeException("script_error", FormatValue(ste.ThrownValue));
            }
        }

        // ---- JSON <-> JS value conversion -------------------------------------------------

        public static object FromJToken(JToken tok)
        {
            if (tok == null || tok.Type == JTokenType.Null || tok.Type == JTokenType.Undefined) return null;
            switch (tok.Type)
            {
                case JTokenType.Boolean: return (bool)tok;
                case JTokenType.Integer: return (double)(long)tok;
                case JTokenType.Float: return (double)tok;
                case JTokenType.String: return (string)tok;
                case JTokenType.Date: return ((DateTime)tok).ToString("o", CultureInfo.InvariantCulture);
                case JTokenType.Array:
                    var list = new List<object>();
                    foreach (JToken item in (JArray)tok) list.Add(FromJToken(item));
                    return list;
                case JTokenType.Object:
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (JProperty p in ((JObject)tok).Properties()) dict[p.Name] = FromJToken(p.Value);
                    return dict;
                default:
                    return tok.ToString(Formatting.None);
            }
        }

        public static JToken ToJToken(object val, int depth = 0)
        {
            if (depth > 32) return JValue.CreateString("[max_depth]");
            if (val == null || val is UndefinedValue) return JValue.CreateNull();
            if (val is JToken jt) return jt.DeepClone();
            if (val is bool b) return new JValue(b);
            if (val is double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) return JValue.CreateNull();
                if (Math.Abs(d - Math.Truncate(d)) < 1e-12 && d >= long.MinValue && d <= long.MaxValue)
                    return new JValue((long)Math.Truncate(d));
                return new JValue(d);
            }
            if (val is int i) return new JValue(i);
            if (val is long l) return new JValue(l);
            if (val is string s) return new JValue(s);
            if (val is List<object> arr)
            {
                var ja = new JArray();
                foreach (object item in arr) ja.Add(ToJToken(item, depth + 1));
                return ja;
            }
            if (val is Dictionary<string, object> obj)
            {
                var jo = new JObject();
                foreach (var kv in obj)
                {
                    if (kv.Value is UndefinedValue) continue;
                    jo[kv.Key] = ToJToken(kv.Value, depth + 1);
                }
                return jo;
            }
            return new JValue(val.ToString());
        }

        internal static bool IsTruthy(object v)
        {
            if (v == null || v is UndefinedValue) return false;
            if (v is bool b) return b;
            if (v is double d) return d != 0.0 && !double.IsNaN(d);
            if (v is string s) return s.Length > 0;
            return true;
        }

        internal static double ToNumber(object v)
        {
            if (v == null) return 0.0;
            if (v is UndefinedValue) return double.NaN;
            if (v is double d) return d;
            if (v is int i) return i;
            if (v is long l) return l;
            if (v is bool b) return b ? 1.0 : 0.0;
            if (v is string s)
            {
                s = s.Trim();
                if (s.Length == 0) return 0.0;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : double.NaN;
            }
            return double.NaN;
        }

        internal static string FormatValue(object v)
        {
            if (v == null) return "null";
            if (v is UndefinedValue) return "undefined";
            if (v is bool b) return b ? "true" : "false";
            if (v is double d) return d.ToString("G15", CultureInfo.InvariantCulture);
            if (v is string s) return s;
            return ToJToken(v).ToString(Formatting.None);
        }

        internal static bool LooseEquals(object a, object b)
        {
            if ((a == null || a is UndefinedValue) && (b == null || b is UndefinedValue)) return true;
            if (a == null || b == null || a is UndefinedValue || b is UndefinedValue) return false;
            if (a is double da && b is double db) return da == db;
            if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
            if (a is bool ba && b is bool bb) return ba == bb;
            if (a is double || b is double) return ToNumber(a) == ToNumber(b);
            return ReferenceEquals(a, b);
        }

        internal static bool StrictEquals(object a, object b)
        {
            if (a == null && b == null) return true;
            if (a is UndefinedValue && b is UndefinedValue) return true;
            if (a == null || b == null || a is UndefinedValue || b is UndefinedValue) return false;
            if (a.GetType() != b.GetType()) return false;
            if (a is double da) return da == (double)b;
            if (a is string sa) return string.Equals(sa, (string)b, StringComparison.Ordinal);
            if (a is bool ba) return ba == (bool)b;
            return ReferenceEquals(a, b);
        }

        private static void RegisterBuiltins(Env env, CodeModeRunOutput output, CodeModeLimits limits)
        {
            // console
            var consoleObj = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["log"] = (HostCallable)(args =>
                {
                    if (output.Logs.Count < limits.MaxLogs)
                    {
                        string line = string.Join(" ", args.Select(FormatValue));
                        if (line.Length > 240) line = line.Substring(0, 240) + "...";
                        output.Logs.Add(line);
                    }
                    return UndefinedValue.Instance;
                })
            };
            env.Define("console", consoleObj);

            // Math
            var mathObj = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["PI"] = Math.PI,
                ["E"] = Math.E,
                ["round"] = (HostCallable)(args => args.Count == 0 ? double.NaN : Math.Round(ToNumber(args[0]), MidpointRounding.AwayFromZero)),
                ["floor"] = (HostCallable)(args => args.Count == 0 ? double.NaN : Math.Floor(ToNumber(args[0]))),
                ["ceil"] = (HostCallable)(args => args.Count == 0 ? double.NaN : Math.Ceiling(ToNumber(args[0]))),
                ["trunc"] = (HostCallable)(args => args.Count == 0 ? double.NaN : Math.Truncate(ToNumber(args[0]))),
                ["abs"] = (HostCallable)(args => args.Count == 0 ? double.NaN : Math.Abs(ToNumber(args[0]))),
                ["sqrt"] = (HostCallable)(args => args.Count == 0 ? double.NaN : Math.Sqrt(ToNumber(args[0]))),
                ["pow"] = (HostCallable)(args => args.Count < 2 ? double.NaN : Math.Pow(ToNumber(args[0]), ToNumber(args[1]))),
                ["min"] = (HostCallable)(args => args.Count == 0 ? double.PositiveInfinity : args.Select(ToNumber).Min()),
                ["max"] = (HostCallable)(args => args.Count == 0 ? double.NegativeInfinity : args.Select(ToNumber).Max())
            };
            env.Define("Math", mathObj);

            // Object
            var objectStatic = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["keys"] = (HostCallable)(args =>
                {
                    if (args.Count > 0 && args[0] is Dictionary<string, object> d)
                        return d.Keys.Cast<object>().ToList();
                    if (args.Count > 0 && args[0] is List<object> l)
                        return Enumerable.Range(0, l.Count).Select(i => (object)i.ToString(CultureInfo.InvariantCulture)).ToList();
                    return new List<object>();
                }),
                ["values"] = (HostCallable)(args =>
                {
                    if (args.Count > 0 && args[0] is Dictionary<string, object> d)
                        return d.Values.ToList();
                    if (args.Count > 0 && args[0] is List<object> l)
                        return new List<object>(l);
                    return new List<object>();
                }),
                ["entries"] = (HostCallable)(args =>
                {
                    var res = new List<object>();
                    if (args.Count > 0 && args[0] is Dictionary<string, object> d)
                    {
                        foreach (var kv in d) res.Add(new List<object> { kv.Key, kv.Value });
                    }
                    return res;
                }),
                ["fromEntries"] = (HostCallable)(args =>
                {
                    var res = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (args.Count > 0 && args[0] is List<object> pairs)
                    {
                        foreach (object item in pairs)
                            if (item is List<object> pair && pair.Count >= 2)
                                res[FormatValue(pair[0])] = pair[1];
                    }
                    return res;
                }),
                ["assign"] = (HostCallable)(args =>
                {
                    if (args.Count == 0 || !(args[0] is Dictionary<string, object> target))
                        return new Dictionary<string, object>(StringComparer.Ordinal);
                    for (int i = 1; i < args.Count; i++)
                        if (args[i] is Dictionary<string, object> src)
                            foreach (var kv in src) target[kv.Key] = kv.Value;
                    return target;
                })
            };
            env.Define("Object", objectStatic);

            // Array
            var arrayStatic = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["isArray"] = (HostCallable)(args => args.Count > 0 && args[0] is List<object>)
            };
            env.Define("Array", arrayStatic);

            // JSON
            var jsonStatic = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["stringify"] = (HostCallable)(args =>
                {
                    if (args.Count == 0) return "null";
                    Formatting fmt = args.Count >= 3 && IsTruthy(args[2]) ? Formatting.Indented : Formatting.None;
                    return ToJToken(args[0]).ToString(fmt);
                }),
                ["parse"] = (HostCallable)(args =>
                {
                    if (args.Count == 0 || !(args[0] is string s))
                        throw new CodeModeException("json_parse", "JSON.parse requires a string.");
                    return FromJToken(JToken.Parse(s));
                })
            };
            env.Define("JSON", jsonStatic);

            // Global helpers
            env.Define("Number", (HostCallable)(args => args.Count == 0 ? 0.0 : ToNumber(args[0])));
            env.Define("String", (HostCallable)(args => args.Count == 0 ? "" : FormatValue(args[0])));
            env.Define("Boolean", (HostCallable)(args => args.Count > 0 && IsTruthy(args[0])));
            env.Define("parseInt", (HostCallable)(args =>
            {
                if (args.Count == 0) return double.NaN;
                string s = FormatValue(args[0]).Trim();
                int radix = args.Count >= 2 ? (int)ToNumber(args[1]) : 10;
                if (radix == 10) return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? (double)v : double.NaN;
                try { return (double)Convert.ToInt64(s, radix); } catch { return double.NaN; }
            }));
            env.Define("parseFloat", (HostCallable)(args => args.Count == 0 ? double.NaN : ToNumber(args[0])));
            env.Define("isNaN", (HostCallable)(args => args.Count == 0 || double.IsNaN(ToNumber(args[0]))));
            env.Define("isFinite", (HostCallable)(args => { if (args.Count == 0) return false; double d = ToNumber(args[0]); return !double.IsNaN(d) && !double.IsInfinity(d); }));
            env.Define("round", (HostCallable)(args =>
            {
                if (args.Count == 0) return double.NaN;
                double v = ToNumber(args[0]);
                int digits = args.Count >= 2 ? Math.Max(0, Math.Min(10, (int)ToNumber(args[1]))) : 0;
                return Math.Round(v, digits, MidpointRounding.AwayFromZero);
            }));
            env.Define("undefined", UndefinedValue.Instance);
            env.Define("NaN", double.NaN);
            env.Define("Infinity", double.PositiveInfinity);
        }

        // =========================================================================
        // LEXER
        // =========================================================================

        private enum TokKind
        {
            Eof, Number, String, Template, Ident,
            LParen, RParen, LBrace, RBrace, LBracket, RBracket,
            Comma, Dot, QuestionDot, Semi, Colon, Question, Arrow,
            Plus, Minus, Star, Slash, Percent,
            PlusPlus, MinusMinus,
            Eq, PlusEq, MinusEq, StarEq, SlashEq,
            EqEq, EqEqEq, BangEq, BangEqEq,
            Lt, LtEq, Gt, GtEq,
            AndAnd, OrOr, QuestionQuestion, Bang
        }

        private readonly struct Token
        {
            public readonly TokKind Kind;
            public readonly string Text;
            public readonly double NumValue;
            public readonly int Line;
            public Token(TokKind k, string t, int line, double num = 0)
            { Kind = k; Text = t; Line = line; NumValue = num; }
        }

        private static class Lexer
        {
            public static List<Token> Tokenize(string src)
            {
                var tokens = new List<Token>();
                int i = 0, n = src.Length, line = 1;

                while (i < n)
                {
                    char c = src[i];
                    if (c == '\r') { i++; continue; }
                    if (c == '\n') { line++; i++; continue; }
                    if (char.IsWhiteSpace(c)) { i++; continue; }

                    // Comments
                    if (c == '/' && i + 1 < n && src[i + 1] == '/')
                    {
                        i += 2;
                        while (i < n && src[i] != '\n') i++;
                        continue;
                    }
                    if (c == '/' && i + 1 < n && src[i + 1] == '*')
                    {
                        i += 2;
                        while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/'))
                        {
                            if (src[i] == '\n') line++;
                            i++;
                        }
                        if (i + 1 >= n) throw new CodeModeException("syntax_error", $"Unterminated block comment at line {line}.");
                        i += 2;
                        continue;
                    }

                    // Strings
                    if (c == '"' || c == '\'')
                    {
                        char quote = c;
                        int startLine = line;
                        i++;
                        var sb = new StringBuilder();
                        while (i < n && src[i] != quote)
                        {
                            if (src[i] == '\n') throw new CodeModeException("syntax_error", $"Unterminated string at line {startLine}.");
                            if (src[i] == '\\' && i + 1 < n)
                            {
                                i++;
                                char esc = src[i++];
                                switch (esc)
                                {
                                    case 'n': sb.Append('\n'); break;
                                    case 'r': sb.Append('\r'); break;
                                    case 't': sb.Append('\t'); break;
                                    case '\\': sb.Append('\\'); break;
                                    case '"': sb.Append('"'); break;
                                    case '\'': sb.Append('\''); break;
                                    case '`': sb.Append('`'); break;
                                    case 'u':
                                        if (i + 4 <= n && int.TryParse(src.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                                        { sb.Append((char)cp); i += 4; }
                                        else sb.Append('u');
                                        break;
                                    default: sb.Append(esc); break;
                                }
                            }
                            else sb.Append(src[i++]);
                        }
                        if (i >= n) throw new CodeModeException("syntax_error", $"Unterminated string at line {startLine}.");
                        i++;
                        tokens.Add(new Token(TokKind.String, sb.ToString(), startLine));
                        continue;
                    }

                    // Template literal (raw text; interpolations ${...} parsed at AST level)
                    if (c == '`')
                    {
                        int startLine = line;
                        i++;
                        var sb = new StringBuilder();
                        while (i < n && src[i] != '`')
                        {
                            if (src[i] == '\n') line++;
                            if (src[i] == '\\' && i + 1 < n)
                            {
                                i++;
                                char esc = src[i++];
                                switch (esc)
                                {
                                    case 'n': sb.Append('\n'); break;
                                    case 'r': sb.Append('\r'); break;
                                    case 't': sb.Append('\t'); break;
                                    case '`': sb.Append('`'); break;
                                    case '$': sb.Append("\\$"); break;
                                    case '\\': sb.Append('\\'); break;
                                    default: sb.Append(esc); break;
                                }
                            }
                            else sb.Append(src[i++]);
                        }
                        if (i >= n) throw new CodeModeException("syntax_error", $"Unterminated template literal at line {startLine}.");
                        i++;
                        tokens.Add(new Token(TokKind.Template, sb.ToString(), startLine));
                        continue;
                    }

                    // Numbers
                    if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(src[i + 1])))
                    {
                        int start = i;
                        while (i < n && (char.IsDigit(src[i]) || src[i] == '.')) i++;
                        if (i < n && (src[i] == 'e' || src[i] == 'E'))
                        {
                            i++;
                            if (i < n && (src[i] == '+' || src[i] == '-')) i++;
                            while (i < n && char.IsDigit(src[i])) i++;
                        }
                        string raw = src.Substring(start, i - start);
                        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                            throw new CodeModeException("syntax_error", $"Invalid number '{raw}' at line {line}.");
                        tokens.Add(new Token(TokKind.Number, raw, line, num));
                        continue;
                    }

                    // Identifiers / keywords
                    if (char.IsLetter(c) || c == '_' || c == '$')
                    {
                        int start = i;
                        while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_' || src[i] == '$')) i++;
                        tokens.Add(new Token(TokKind.Ident, src.Substring(start, i - start), line));
                        continue;
                    }

                    // 3-char & 2-char operators
                    string two = i + 1 < n ? src.Substring(i, 2) : "";
                    string three = i + 2 < n ? src.Substring(i, 3) : "";
                    if (three == "===") { tokens.Add(new Token(TokKind.EqEqEq, three, line)); i += 3; continue; }
                    if (three == "!==") { tokens.Add(new Token(TokKind.BangEqEq, three, line)); i += 3; continue; }
                    if (two == "=>") { tokens.Add(new Token(TokKind.Arrow, two, line)); i += 2; continue; }
                    if (two == "==") { tokens.Add(new Token(TokKind.EqEq, two, line)); i += 2; continue; }
                    if (two == "!=") { tokens.Add(new Token(TokKind.BangEq, two, line)); i += 2; continue; }
                    if (two == "<=") { tokens.Add(new Token(TokKind.LtEq, two, line)); i += 2; continue; }
                    if (two == ">=") { tokens.Add(new Token(TokKind.GtEq, two, line)); i += 2; continue; }
                    if (two == "&&") { tokens.Add(new Token(TokKind.AndAnd, two, line)); i += 2; continue; }
                    if (two == "||") { tokens.Add(new Token(TokKind.OrOr, two, line)); i += 2; continue; }
                    if (two == "??") { tokens.Add(new Token(TokKind.QuestionQuestion, two, line)); i += 2; continue; }
                    if (two == "?.") { tokens.Add(new Token(TokKind.QuestionDot, two, line)); i += 2; continue; }
                    if (two == "++") { tokens.Add(new Token(TokKind.PlusPlus, two, line)); i += 2; continue; }
                    if (two == "--") { tokens.Add(new Token(TokKind.MinusMinus, two, line)); i += 2; continue; }
                    if (two == "+=") { tokens.Add(new Token(TokKind.PlusEq, two, line)); i += 2; continue; }
                    if (two == "-=") { tokens.Add(new Token(TokKind.MinusEq, two, line)); i += 2; continue; }
                    if (two == "*=") { tokens.Add(new Token(TokKind.StarEq, two, line)); i += 2; continue; }
                    if (two == "/=") { tokens.Add(new Token(TokKind.SlashEq, two, line)); i += 2; continue; }

                    switch (c)
                    {
                        case '(': tokens.Add(new Token(TokKind.LParen, "(", line)); i++; continue;
                        case ')': tokens.Add(new Token(TokKind.RParen, ")", line)); i++; continue;
                        case '{': tokens.Add(new Token(TokKind.LBrace, "{", line)); i++; continue;
                        case '}': tokens.Add(new Token(TokKind.RBrace, "}", line)); i++; continue;
                        case '[': tokens.Add(new Token(TokKind.LBracket, "[", line)); i++; continue;
                        case ']': tokens.Add(new Token(TokKind.RBracket, "]", line)); i++; continue;
                        case ',': tokens.Add(new Token(TokKind.Comma, ",", line)); i++; continue;
                        case '.': tokens.Add(new Token(TokKind.Dot, ".", line)); i++; continue;
                        case ';': tokens.Add(new Token(TokKind.Semi, ";", line)); i++; continue;
                        case ':': tokens.Add(new Token(TokKind.Colon, ":", line)); i++; continue;
                        case '?': tokens.Add(new Token(TokKind.Question, "?", line)); i++; continue;
                        case '+': tokens.Add(new Token(TokKind.Plus, "+", line)); i++; continue;
                        case '-': tokens.Add(new Token(TokKind.Minus, "-", line)); i++; continue;
                        case '*': tokens.Add(new Token(TokKind.Star, "*", line)); i++; continue;
                        case '/': tokens.Add(new Token(TokKind.Slash, "/", line)); i++; continue;
                        case '%': tokens.Add(new Token(TokKind.Percent, "%", line)); i++; continue;
                        case '=': tokens.Add(new Token(TokKind.Eq, "=", line)); i++; continue;
                        case '<': tokens.Add(new Token(TokKind.Lt, "<", line)); i++; continue;
                        case '>': tokens.Add(new Token(TokKind.Gt, ">", line)); i++; continue;
                        case '!': tokens.Add(new Token(TokKind.Bang, "!", line)); i++; continue;
                    }
                    throw new CodeModeException("syntax_error", $"Unexpected character '{c}' at line {line}.");
                }
                tokens.Add(new Token(TokKind.Eof, "", line));
                return tokens;
            }
        }

        // =========================================================================
        // AST NODES
        // =========================================================================

        private abstract class Stmt { }
        private abstract class Expr { }

        private sealed class BlockStmt : Stmt { public List<Stmt> Body; }
        private sealed class ExprStmt : Stmt { public Expr Expression; }
        private sealed class VarDeclStmt : Stmt { public string Name; public Expr Init; }
        private sealed class IfStmt : Stmt { public Expr Cond; public Stmt Then; public Stmt Else; }
        private sealed class WhileStmt : Stmt { public Expr Cond; public Stmt Body; }
        private sealed class ForStmt : Stmt { public Stmt Init; public Expr Cond; public Expr Post; public Stmt Body; }
        private sealed class ForOfStmt : Stmt { public string VarName; public Expr Iterable; public Stmt Body; public bool IsIn; }
        private sealed class ReturnStmt : Stmt { public Expr Value; }
        private sealed class BreakStmt : Stmt { }
        private sealed class ContinueStmt : Stmt { }
        private sealed class ThrowStmt : Stmt { public Expr Value; }
        private sealed class TryCatchStmt : Stmt { public List<Stmt> TryBody; public string CatchVar; public List<Stmt> CatchBody; }
        private sealed class FuncDeclStmt : Stmt { public string Name; public List<string> Params; public List<Stmt> Body; }

        private sealed class LiteralExpr : Expr { public object Value; }
        private sealed class TemplateExpr : Expr { public List<Expr> Parts; }
        private sealed class IdentExpr : Expr { public string Name; }
        private sealed class ArrayExpr : Expr { public List<Expr> Elements; }
        private sealed class ObjectExpr : Expr { public List<(string Key, Expr Value)> Props; }
        private sealed class UnaryExpr : Expr { public string Op; public Expr Operand; public bool Postfix; }
        private sealed class BinaryExpr : Expr { public string Op; public Expr Left; public Expr Right; }
        private sealed class TernaryExpr : Expr { public Expr Cond; public Expr Then; public Expr Else; }
        private sealed class AssignExpr : Expr { public Expr Target; public string Op; public Expr Value; }
        private sealed class MemberExpr : Expr { public Expr Object; public string Prop; public bool Optional; }
        private sealed class IndexExpr : Expr { public Expr Object; public Expr Index; public bool Optional; }
        private sealed class CallExpr : Expr { public Expr Callee; public List<Expr> Args; }
        private sealed class FuncExpr : Expr { public List<string> Params; public List<Stmt> Body; public Expr ExprBody; }

        // =========================================================================
        // PARSER
        // =========================================================================

        private sealed class Parser
        {
            private readonly List<Token> _t;
            private int _p;

            public Parser(List<Token> tokens) { _t = tokens; }

            private Token Cur => _t[_p];
            private Token Peek(int offset) => _p + offset < _t.Count ? _t[_p + offset] : _t[_t.Count - 1];
            private bool At(TokKind k) => Cur.Kind == k;
            private bool AtKw(string kw) => Cur.Kind == TokKind.Ident && Cur.Text == kw;
            private Token Advance() => _t[_p++];
            private bool Match(TokKind k) { if (At(k)) { _p++; return true; } return false; }
            private bool MatchKw(string kw) { if (AtKw(kw)) { _p++; return true; } return false; }

            private Token Expect(TokKind k, string msg)
            {
                if (!At(k)) throw new CodeModeException("syntax_error", $"{msg} at line {Cur.Line} (got '{Cur.Text}').");
                return Advance();
            }

            public List<Stmt> ParseProgram()
            {
                var stmts = new List<Stmt>();
                while (!At(TokKind.Eof))
                {
                    if (Match(TokKind.Semi)) continue;
                    stmts.Add(ParseStmt());
                }
                return stmts;
            }

            private List<Stmt> ParseBlock()
            {
                Expect(TokKind.LBrace, "Expected '{'");
                var stmts = new List<Stmt>();
                while (!At(TokKind.RBrace) && !At(TokKind.Eof))
                {
                    if (Match(TokKind.Semi)) continue;
                    stmts.Add(ParseStmt());
                }
                Expect(TokKind.RBrace, "Expected '}'");
                return stmts;
            }

            private Stmt ParseStmt()
            {
                if (At(TokKind.LBrace)) return new BlockStmt { Body = ParseBlock() };
                if (AtKw("let") || AtKw("const") || AtKw("var")) return ParseVarDecl(consumeSemi: true);
                if (MatchKw("if"))
                {
                    Expect(TokKind.LParen, "Expected '(' after if");
                    Expr cond = ParseExpr();
                    Expect(TokKind.RParen, "Expected ')' after if condition");
                    Stmt thenS = ParseStmt();
                    Stmt elseS = MatchKw("else") ? ParseStmt() : null;
                    return new IfStmt { Cond = cond, Then = thenS, Else = elseS };
                }
                if (MatchKw("while"))
                {
                    Expect(TokKind.LParen, "Expected '(' after while");
                    Expr cond = ParseExpr();
                    Expect(TokKind.RParen, "Expected ')' after while condition");
                    return new WhileStmt { Cond = cond, Body = ParseStmt() };
                }
                if (MatchKw("for")) return ParseFor();
                if (MatchKw("return"))
                {
                    Expr val = (At(TokKind.Semi) || At(TokKind.RBrace) || At(TokKind.Eof)) ? null : ParseExpr();
                    Match(TokKind.Semi);
                    return new ReturnStmt { Value = val };
                }
                if (MatchKw("break")) { Match(TokKind.Semi); return new BreakStmt(); }
                if (MatchKw("continue")) { Match(TokKind.Semi); return new ContinueStmt(); }
                if (MatchKw("throw"))
                {
                    Expr val = ParseExpr();
                    Match(TokKind.Semi);
                    return new ThrowStmt { Value = val };
                }
                if (MatchKw("try"))
                {
                    var tryBody = ParseBlock();
                    string catchVar = null;
                    List<Stmt> catchBody = new List<Stmt>();
                    if (MatchKw("catch"))
                    {
                        if (Match(TokKind.LParen))
                        {
                            catchVar = Expect(TokKind.Ident, "Expected catch variable").Text;
                            Expect(TokKind.RParen, "Expected ')'");
                        }
                        catchBody = ParseBlock();
                    }
                    return new TryCatchStmt { TryBody = tryBody, CatchVar = catchVar, CatchBody = catchBody };
                }
                if (AtKw("async") && Peek(1).Kind == TokKind.Ident && Peek(1).Text == "function")
                    Advance(); // accept `async function` transparently (host calls are synchronous in sandbox)
                if (MatchKw("function"))
                {
                    string name = Expect(TokKind.Ident, "Expected function name").Text;
                    var parms = ParseParamList();
                    var body = ParseBlock();
                    return new FuncDeclStmt { Name = name, Params = parms, Body = body };
                }

                Expr expr = ParseExpr();
                Match(TokKind.Semi);
                return new ExprStmt { Expression = expr };
            }

            private Stmt ParseVarDecl(bool consumeSemi)
            {
                Advance(); // let/const/var
                string name = Expect(TokKind.Ident, "Expected variable name").Text;
                Expr init = Match(TokKind.Eq) ? ParseExpr() : null;
                if (consumeSemi) Match(TokKind.Semi);
                return new VarDeclStmt { Name = name, Init = init };
            }

            private Stmt ParseFor()
            {
                Expect(TokKind.LParen, "Expected '(' after for");
                // Detect for (let x of/in expr)
                int save = _p;
                if (AtKw("let") || AtKw("const") || AtKw("var")) Advance();
                if (At(TokKind.Ident) && (Peek(1).Kind == TokKind.Ident && (Peek(1).Text == "of" || Peek(1).Text == "in")))
                {
                    string vname = Advance().Text;
                    bool isIn = Advance().Text == "in";
                    Expr iter = ParseExpr();
                    Expect(TokKind.RParen, "Expected ')' in for-of/in");
                    return new ForOfStmt { VarName = vname, Iterable = iter, Body = ParseStmt(), IsIn = isIn };
                }
                _p = save;

                Stmt init = null;
                if (!At(TokKind.Semi))
                {
                    init = (AtKw("let") || AtKw("const") || AtKw("var"))
                        ? ParseVarDecl(consumeSemi: false)
                        : new ExprStmt { Expression = ParseExpr() };
                }
                Expect(TokKind.Semi, "Expected ';' after for initializer");
                Expr cond = At(TokKind.Semi) ? null : ParseExpr();
                Expect(TokKind.Semi, "Expected ';' after for condition");
                Expr post = At(TokKind.RParen) ? null : ParseExpr();
                Expect(TokKind.RParen, "Expected ')' after for clauses");
                return new ForStmt { Init = init, Cond = cond, Post = post, Body = ParseStmt() };
            }

            private List<string> ParseParamList()
            {
                Expect(TokKind.LParen, "Expected '('");
                var parms = new List<string>();
                while (!At(TokKind.RParen) && !At(TokKind.Eof))
                {
                    parms.Add(Expect(TokKind.Ident, "Expected parameter name").Text);
                    if (!Match(TokKind.Comma)) break;
                }
                Expect(TokKind.RParen, "Expected ')'");
                return parms;
            }

            private Expr ParseExpr() => ParseAssign();

            private Expr ParseAssign()
            {
                // Arrow function: x => ... or (a, b) => ... or async (...) => ...
                if (TryParseArrow(out Expr arrow)) return arrow;

                Expr left = ParseTernary();
                if (At(TokKind.Eq) || At(TokKind.PlusEq) || At(TokKind.MinusEq) || At(TokKind.StarEq) || At(TokKind.SlashEq))
                {
                    string op = Advance().Text;
                    Expr right = ParseAssign();
                    return new AssignExpr { Target = left, Op = op, Value = right };
                }
                return left;
            }

            private bool TryParseArrow(out Expr arrow)
            {
                arrow = null;
                int save = _p;
                if (AtKw("async")) Advance();

                List<string> parms = null;
                if (At(TokKind.Ident) && Peek(1).Kind == TokKind.Arrow)
                {
                    parms = new List<string> { Advance().Text };
                    Advance(); // =>
                }
                else if (At(TokKind.LParen))
                {
                    Advance();
                    var candidate = new List<string>();
                    bool valid = true;
                    while (!At(TokKind.RParen) && !At(TokKind.Eof))
                    {
                        if (!At(TokKind.Ident)) { valid = false; break; }
                        candidate.Add(Advance().Text);
                        if (!Match(TokKind.Comma)) break;
                    }
                    if (valid && Match(TokKind.RParen) && Match(TokKind.Arrow))
                        parms = candidate;
                }

                if (parms == null) { _p = save; return false; }

                if (At(TokKind.LBrace))
                    arrow = new FuncExpr { Params = parms, Body = ParseBlock() };
                else
                    arrow = new FuncExpr { Params = parms, ExprBody = ParseAssign() };
                return true;
            }

            private Expr ParseTernary()
            {
                Expr cond = ParseNullish();
                if (Match(TokKind.Question))
                {
                    Expr thenE = ParseAssign();
                    Expect(TokKind.Colon, "Expected ':' in ternary");
                    Expr elseE = ParseAssign();
                    return new TernaryExpr { Cond = cond, Then = thenE, Else = elseE };
                }
                return cond;
            }

            private Expr ParseNullish()
            {
                Expr e = ParseOr();
                while (Match(TokKind.QuestionQuestion)) e = new BinaryExpr { Op = "??", Left = e, Right = ParseOr() };
                return e;
            }

            private Expr ParseOr()
            {
                Expr e = ParseAnd();
                while (Match(TokKind.OrOr)) e = new BinaryExpr { Op = "||", Left = e, Right = ParseAnd() };
                return e;
            }

            private Expr ParseAnd()
            {
                Expr e = ParseEquality();
                while (Match(TokKind.AndAnd)) e = new BinaryExpr { Op = "&&", Left = e, Right = ParseEquality() };
                return e;
            }

            private Expr ParseEquality()
            {
                Expr e = ParseCompare();
                while (At(TokKind.EqEq) || At(TokKind.EqEqEq) || At(TokKind.BangEq) || At(TokKind.BangEqEq))
                    e = new BinaryExpr { Op = Advance().Text, Left = e, Right = ParseCompare() };
                return e;
            }

            private Expr ParseCompare()
            {
                Expr e = ParseAdd();
                while (At(TokKind.Lt) || At(TokKind.LtEq) || At(TokKind.Gt) || At(TokKind.GtEq) || AtKw("in"))
                    e = new BinaryExpr { Op = Advance().Text, Left = e, Right = ParseAdd() };
                return e;
            }

            private Expr ParseAdd()
            {
                Expr e = ParseMul();
                while (At(TokKind.Plus) || At(TokKind.Minus))
                    e = new BinaryExpr { Op = Advance().Text, Left = e, Right = ParseMul() };
                return e;
            }

            private Expr ParseMul()
            {
                Expr e = ParseUnary();
                while (At(TokKind.Star) || At(TokKind.Slash) || At(TokKind.Percent))
                    e = new BinaryExpr { Op = Advance().Text, Left = e, Right = ParseUnary() };
                return e;
            }

            private Expr ParseUnary()
            {
                if (MatchKw("await")) return ParseUnary(); // accept `await` transparently
                if (MatchKw("typeof")) return new UnaryExpr { Op = "typeof", Operand = ParseUnary() };
                if (At(TokKind.Bang) || At(TokKind.Minus) || At(TokKind.Plus) || At(TokKind.PlusPlus) || At(TokKind.MinusMinus))
                    return new UnaryExpr { Op = Advance().Text, Operand = ParseUnary() };
                return ParsePostfix();
            }

            private Expr ParsePostfix()
            {
                Expr e = ParsePrimary();
                while (true)
                {
                    if (Match(TokKind.Dot))
                    {
                        string prop = Expect(TokKind.Ident, "Expected property name after '.'").Text;
                        e = new MemberExpr { Object = e, Prop = prop, Optional = false };
                    }
                    else if (Match(TokKind.QuestionDot))
                    {
                        if (Match(TokKind.LBracket))
                        {
                            Expr idx = ParseExpr();
                            Expect(TokKind.RBracket, "Expected ']'");
                            e = new IndexExpr { Object = e, Index = idx, Optional = true };
                        }
                        else
                        {
                            string prop = Expect(TokKind.Ident, "Expected property name after '?.'").Text;
                            e = new MemberExpr { Object = e, Prop = prop, Optional = true };
                        }
                    }
                    else if (Match(TokKind.LBracket))
                    {
                        Expr idx = ParseExpr();
                        Expect(TokKind.RBracket, "Expected ']'");
                        e = new IndexExpr { Object = e, Index = idx, Optional = false };
                    }
                    else if (Match(TokKind.LParen))
                    {
                        var args = new List<Expr>();
                        while (!At(TokKind.RParen) && !At(TokKind.Eof))
                        {
                            args.Add(ParseExpr());
                            if (!Match(TokKind.Comma)) break;
                        }
                        Expect(TokKind.RParen, "Expected ')'");
                        e = new CallExpr { Callee = e, Args = args };
                    }
                    else if (At(TokKind.PlusPlus) || At(TokKind.MinusMinus))
                    {
                        e = new UnaryExpr { Op = Advance().Text, Operand = e, Postfix = true };
                    }
                    else break;
                }
                return e;
            }

            private Expr ParsePrimary()
            {
                if (At(TokKind.Number)) return new LiteralExpr { Value = Advance().NumValue };
                if (At(TokKind.String)) return new LiteralExpr { Value = Advance().Text };
                if (At(TokKind.Template)) return ParseTemplate(Advance().Text);
                if (MatchKw("true")) return new LiteralExpr { Value = true };
                if (MatchKw("false")) return new LiteralExpr { Value = false };
                if (MatchKw("null")) return new LiteralExpr { Value = null };
                if (MatchKw("function"))
                {
                    if (At(TokKind.Ident)) Advance(); // optional name
                    var parms = ParseParamList();
                    var body = ParseBlock();
                    return new FuncExpr { Params = parms, Body = body };
                }
                if (At(TokKind.Ident)) return new IdentExpr { Name = Advance().Text };
                if (Match(TokKind.LParen))
                {
                    Expr inner = ParseExpr();
                    Expect(TokKind.RParen, "Expected ')'");
                    return inner;
                }
                if (Match(TokKind.LBracket))
                {
                    var elems = new List<Expr>();
                    while (!At(TokKind.RBracket) && !At(TokKind.Eof))
                    {
                        elems.Add(ParseExpr());
                        if (!Match(TokKind.Comma)) break;
                    }
                    Expect(TokKind.RBracket, "Expected ']'");
                    return new ArrayExpr { Elements = elems };
                }
                if (Match(TokKind.LBrace))
                {
                    var props = new List<(string, Expr)>();
                    while (!At(TokKind.RBrace) && !At(TokKind.Eof))
                    {
                        string key;
                        bool shorthand = false;
                        if (At(TokKind.String)) key = Advance().Text;
                        else if (At(TokKind.Ident)) { key = Advance().Text; shorthand = true; }
                        else if (At(TokKind.Number)) key = Advance().Text;
                        else throw new CodeModeException("syntax_error", $"Expected object key at line {Cur.Line}.");

                        Expr val;
                        if (Match(TokKind.Colon)) val = ParseExpr();
                        else if (shorthand) val = new IdentExpr { Name = key };
                        else throw new CodeModeException("syntax_error", $"Expected ':' after key '{key}' at line {Cur.Line}.");

                        props.Add((key, val));
                        if (!Match(TokKind.Comma)) break;
                    }
                    Expect(TokKind.RBrace, "Expected '}'");
                    return new ObjectExpr { Props = props };
                }
                throw new CodeModeException("syntax_error", $"Unexpected token '{Cur.Text}' at line {Cur.Line}.");
            }

            private Expr ParseTemplate(string raw)
            {
                var parts = new List<Expr>();
                int i = 0, n = raw.Length;
                var lit = new StringBuilder();
                while (i < n)
                {
                    if (raw[i] == '\\' && i + 1 < n && raw[i + 1] == '$')
                    {
                        lit.Append('$'); i += 2; continue;
                    }
                    if (raw[i] == '$' && i + 1 < n && raw[i + 1] == '{')
                    {
                        if (lit.Length > 0) { parts.Add(new LiteralExpr { Value = lit.ToString() }); lit.Clear(); }
                        i += 2;
                        int depth = 1, start = i;
                        while (i < n && depth > 0)
                        {
                            if (raw[i] == '{') depth++;
                            else if (raw[i] == '}') depth--;
                            if (depth > 0) i++;
                        }
                        if (depth != 0) throw new CodeModeException("syntax_error", "Unterminated '${...}' in template literal.");
                        string innerSrc = raw.Substring(start, i - start);
                        i++; // '}'
                        var subTokens = Lexer.Tokenize(innerSrc);
                        parts.Add(new Parser(subTokens).ParseExpr());
                    }
                    else lit.Append(raw[i++]);
                }
                if (lit.Length > 0 || parts.Count == 0)
                    parts.Add(new LiteralExpr { Value = lit.ToString() });
                return new TemplateExpr { Parts = parts };
            }
        }

        // =========================================================================
        // EVALUATOR
        // =========================================================================

        private enum FlowKind { Normal, Return, Break, Continue }

        private readonly struct Flow
        {
            public readonly FlowKind Kind;
            public readonly object Value;
            public readonly bool HasValue;
            public Flow(FlowKind k, object v = null, bool hasValue = false) { Kind = k; Value = v; HasValue = hasValue; }
        }

        private sealed class ScriptThrowException : Exception
        {
            public object ThrownValue { get; }
            public ScriptThrowException(object v) { ThrownValue = v; }
        }

        private sealed class Env
        {
            private readonly Env _parent;
            private readonly Dictionary<string, object> _vars = new Dictionary<string, object>(StringComparer.Ordinal);
            public Env(Env parent) { _parent = parent; }

            public void Define(string name, object val) => _vars[name] = val;

            public object Get(string name)
            {
                if (_vars.TryGetValue(name, out object v)) return v;
                if (_parent != null) return _parent.Get(name);
                throw new CodeModeException("reference_error", $"Undefined identifier '{name}'.");
            }

            public void Set(string name, object val)
            {
                if (_vars.ContainsKey(name)) { _vars[name] = val; return; }
                if (_parent != null && _parent.TrySet(name, val)) return;
                _vars[name] = val;
            }

            private bool TrySet(string name, object val)
            {
                if (_vars.ContainsKey(name)) { _vars[name] = val; return true; }
                return _parent != null && _parent.TrySet(name, val);
            }
        }

        private sealed class Closure
        {
            public List<string> Params;
            public List<Stmt> Body;
            public Expr ExprBody;
            public Env Captured;
        }

        private sealed class Evaluator
        {
            private readonly CodeModeLimits _limits;
            private readonly DateTime _deadline;
            private readonly CancellationToken _ct;
            private readonly CodeModeRunOutput _out;
            private int _depth;
            public int Steps { get; private set; }

            public Evaluator(CodeModeLimits limits, DateTime deadline, CancellationToken ct, CodeModeRunOutput output)
            {
                _limits = limits; _deadline = deadline; _ct = ct; _out = output;
            }

            private void Tick()
            {
                Steps++;
                if (Steps > _limits.MaxSteps)
                    throw new CodeModeException("step_limit", $"Script exceeded max_steps ({_limits.MaxSteps}). Check for infinite loops.");
                if ((Steps & 255) == 0)
                {
                    if (_ct.IsCancellationRequested)
                        throw new CodeModeException("cancelled", "Script execution cancelled.");
                    if (DateTime.UtcNow > _deadline)
                        throw new CodeModeException("timeout", $"Script exceeded timeout_ms ({_limits.TimeoutMs} ms).");
                }
            }

            public Flow ExecStmt(Stmt s, Env env)
            {
                Tick();
                switch (s)
                {
                    case BlockStmt bs:
                        var sub = new Env(env);
                        Flow last = new Flow(FlowKind.Normal);
                        foreach (Stmt inner in bs.Body)
                        {
                            last = ExecStmt(inner, sub);
                            if (last.Kind != FlowKind.Normal) return last;
                        }
                        return last;

                    case ExprStmt es:
                        return new Flow(FlowKind.Normal, Eval(es.Expression, env), true);

                    case VarDeclStmt vd:
                        env.Define(vd.Name, vd.Init == null ? UndefinedValue.Instance : Eval(vd.Init, env));
                        return new Flow(FlowKind.Normal);

                    case FuncDeclStmt fd:
                        env.Define(fd.Name, new Closure { Params = fd.Params, Body = fd.Body, Captured = env });
                        return new Flow(FlowKind.Normal);

                    case IfStmt ifs:
                        if (IsTruthy(Eval(ifs.Cond, env))) return ExecStmt(ifs.Then, env);
                        return ifs.Else != null ? ExecStmt(ifs.Else, env) : new Flow(FlowKind.Normal);

                    case WhileStmt ws:
                        while (IsTruthy(Eval(ws.Cond, env)))
                        {
                            Tick();
                            Flow r = ExecStmt(ws.Body, env);
                            if (r.Kind == FlowKind.Break) break;
                            if (r.Kind == FlowKind.Return) return r;
                        }
                        return new Flow(FlowKind.Normal);

                    case ForStmt fs:
                        var fEnv = new Env(env);
                        if (fs.Init != null) ExecStmt(fs.Init, fEnv);
                        while (fs.Cond == null || IsTruthy(Eval(fs.Cond, fEnv)))
                        {
                            Tick();
                            Flow r = ExecStmt(fs.Body, fEnv);
                            if (r.Kind == FlowKind.Break) break;
                            if (r.Kind == FlowKind.Return) return r;
                            if (fs.Post != null) Eval(fs.Post, fEnv);
                        }
                        return new Flow(FlowKind.Normal);

                    case ForOfStmt fo:
                        object iterVal = Eval(fo.Iterable, env);
                        IEnumerable<object> seq;
                        if (fo.IsIn)
                        {
                            if (iterVal is Dictionary<string, object> d) seq = d.Keys.Cast<object>();
                            else if (iterVal is List<object> l) seq = Enumerable.Range(0, l.Count).Select(i => (object)i.ToString(CultureInfo.InvariantCulture));
                            else seq = Array.Empty<object>();
                        }
                        else
                        {
                            if (iterVal is List<object> l) seq = l;
                            else if (iterVal is string str) seq = str.Select(ch => (object)ch.ToString());
                            else throw new CodeModeException("type_error", "for..of target is not iterable.");
                        }
                        foreach (object item in seq)
                        {
                            Tick();
                            var loopEnv = new Env(env);
                            loopEnv.Define(fo.VarName, item);
                            Flow r = ExecStmt(fo.Body, loopEnv);
                            if (r.Kind == FlowKind.Break) break;
                            if (r.Kind == FlowKind.Return) return r;
                        }
                        return new Flow(FlowKind.Normal);

                    case ReturnStmt rs:
                        return new Flow(FlowKind.Return, rs.Value == null ? UndefinedValue.Instance : Eval(rs.Value, env), true);

                    case BreakStmt _:
                        return new Flow(FlowKind.Break);

                    case ContinueStmt _:
                        return new Flow(FlowKind.Continue);

                    case ThrowStmt ts:
                        throw new ScriptThrowException(Eval(ts.Value, env));

                    case TryCatchStmt tc:
                        try
                        {
                            var tryEnv = new Env(env);
                            foreach (Stmt ts2 in tc.TryBody)
                            {
                                Flow r = ExecStmt(ts2, tryEnv);
                                if (r.Kind != FlowKind.Normal) return r;
                            }
                        }
                        catch (ScriptThrowException ste)
                        {
                            var cEnv = new Env(env);
                            if (tc.CatchVar != null) cEnv.Define(tc.CatchVar, ste.ThrownValue);
                            foreach (Stmt cs in tc.CatchBody)
                            {
                                Flow r = ExecStmt(cs, cEnv);
                                if (r.Kind != FlowKind.Normal) return r;
                            }
                        }
                        catch (CodeModeException cme) when (cme.Code == "tool_error")
                        {
                            var cEnv = new Env(env);
                            if (tc.CatchVar != null)
                            {
                                cEnv.Define(tc.CatchVar, new Dictionary<string, object>(StringComparer.Ordinal)
                                {
                                    ["code"] = cme.Code,
                                    ["message"] = cme.Message
                                });
                            }
                            foreach (Stmt cs in tc.CatchBody)
                            {
                                Flow r = ExecStmt(cs, cEnv);
                                if (r.Kind != FlowKind.Normal) return r;
                            }
                        }
                        return new Flow(FlowKind.Normal);
                }
                return new Flow(FlowKind.Normal);
            }

            public object Eval(Expr e, Env env)
            {
                Tick();
                switch (e)
                {
                    case LiteralExpr lit: return lit.Value;
                    case IdentExpr id: return env.Get(id.Name);
                    case TemplateExpr te:
                        var sb = new StringBuilder();
                        foreach (Expr p in te.Parts) sb.Append(FormatValue(Eval(p, env)));
                        return sb.ToString();
                    case ArrayExpr ae:
                        var list = new List<object>(ae.Elements.Count);
                        foreach (Expr el in ae.Elements) list.Add(Eval(el, env));
                        return list;
                    case ObjectExpr oe:
                        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                        foreach (var (k, v) in oe.Props) dict[k] = Eval(v, env);
                        return dict;
                    case FuncExpr fe:
                        return new Closure { Params = fe.Params, Body = fe.Body, ExprBody = fe.ExprBody, Captured = env };
                    case TernaryExpr te:
                        return IsTruthy(Eval(te.Cond, env)) ? Eval(te.Then, env) : Eval(te.Else, env);
                    case UnaryExpr ue:
                        return EvalUnary(ue, env);
                    case BinaryExpr be:
                        return EvalBinary(be, env);
                    case AssignExpr ae:
                        return EvalAssign(ae, env);
                    case MemberExpr me:
                        return GetMember(Eval(me.Object, env), me.Prop, me.Optional);
                    case IndexExpr ie:
                        return GetIndex(Eval(ie.Object, env), Eval(ie.Index, env), ie.Optional);
                    case CallExpr ce:
                        return EvalCall(ce, env);
                }
                return UndefinedValue.Instance;
            }

            private object EvalUnary(UnaryExpr ue, Env env)
            {
                if (ue.Op == "++")
                {
                    double cur = ToNumber(Eval(ue.Operand, env));
                    double next = cur + 1.0;
                    AssignTarget(ue.Operand, next, env);
                    return ue.Postfix ? cur : next;
                }
                if (ue.Op == "--")
                {
                    double cur = ToNumber(Eval(ue.Operand, env));
                    double next = cur - 1.0;
                    AssignTarget(ue.Operand, next, env);
                    return ue.Postfix ? cur : next;
                }
                object v = Eval(ue.Operand, env);
                switch (ue.Op)
                {
                    case "!": return !IsTruthy(v);
                    case "-": return -ToNumber(v);
                    case "+": return ToNumber(v);
                    case "typeof":
                        if (v is UndefinedValue) return "undefined";
                        if (v == null) return "object";
                        if (v is bool) return "boolean";
                        if (v is double) return "number";
                        if (v is string) return "string";
                        if (v is Closure || v is HostCallable) return "function";
                        return "object";
                }
                return UndefinedValue.Instance;
            }

            private object EvalBinary(BinaryExpr be, Env env)
            {
                if (be.Op == "&&") { object l = Eval(be.Left, env); return IsTruthy(l) ? Eval(be.Right, env) : l; }
                if (be.Op == "||") { object l = Eval(be.Left, env); return IsTruthy(l) ? l : Eval(be.Right, env); }
                if (be.Op == "??") { object l = Eval(be.Left, env); return (l == null || l is UndefinedValue) ? Eval(be.Right, env) : l; }

                object a = Eval(be.Left, env);
                object b = Eval(be.Right, env);
                switch (be.Op)
                {
                    case "+":
                        if (a is string || b is string) return FormatValue(a) + FormatValue(b);
                        return ToNumber(a) + ToNumber(b);
                    case "-": return ToNumber(a) - ToNumber(b);
                    case "*": return ToNumber(a) * ToNumber(b);
                    case "/": return ToNumber(a) / ToNumber(b);
                    case "%": return ToNumber(a) % ToNumber(b);
                    case "==": return LooseEquals(a, b);
                    case "!=": return !LooseEquals(a, b);
                    case "===": return StrictEquals(a, b);
                    case "!==": return !StrictEquals(a, b);
                    case "<": return (a is string s1 && b is string s2) ? string.CompareOrdinal(s1, s2) < 0 : ToNumber(a) < ToNumber(b);
                    case "<=": return (a is string s3 && b is string s4) ? string.CompareOrdinal(s3, s4) <= 0 : ToNumber(a) <= ToNumber(b);
                    case ">": return (a is string s5 && b is string s6) ? string.CompareOrdinal(s5, s6) > 0 : ToNumber(a) > ToNumber(b);
                    case ">=": return (a is string s7 && b is string s8) ? string.CompareOrdinal(s7, s8) >= 0 : ToNumber(a) >= ToNumber(b);
                    case "in":
                        string key = FormatValue(a);
                        if (b is Dictionary<string, object> dict) return dict.ContainsKey(key);
                        if (b is DynamicHostObject dyn) return dyn.TryGet(key, out _);
                        return false;
                }
                return UndefinedValue.Instance;
            }

            private object EvalAssign(AssignExpr ae, Env env)
            {
                object rhs = Eval(ae.Value, env);
                if (ae.Op != "=")
                {
                    object cur = Eval(ae.Target, env);
                    switch (ae.Op)
                    {
                        case "+=": rhs = (cur is string || rhs is string) ? (object)(FormatValue(cur) + FormatValue(rhs)) : ToNumber(cur) + ToNumber(rhs); break;
                        case "-=": rhs = ToNumber(cur) - ToNumber(rhs); break;
                        case "*=": rhs = ToNumber(cur) * ToNumber(rhs); break;
                        case "/=": rhs = ToNumber(cur) / ToNumber(rhs); break;
                    }
                }
                AssignTarget(ae.Target, rhs, env);
                return rhs;
            }

            private void AssignTarget(Expr target, object val, Env env)
            {
                if (target is IdentExpr id) { env.Set(id.Name, val); return; }
                if (target is MemberExpr me)
                {
                    object obj = Eval(me.Object, env);
                    if (obj is Dictionary<string, object> dict) { dict[me.Prop] = val; return; }
                    throw new CodeModeException("type_error", $"Cannot assign property '{me.Prop}' on non-object.");
                }
                if (target is IndexExpr ie)
                {
                    object obj = Eval(ie.Object, env);
                    object idx = Eval(ie.Index, env);
                    if (obj is Dictionary<string, object> dict) { dict[FormatValue(idx)] = val; return; }
                    if (obj is List<object> list)
                    {
                        int i = (int)ToNumber(idx);
                        if (i >= 0 && i < list.Count) { list[i] = val; return; }
                        if (i == list.Count) { list.Add(val); return; }
                    }
                    throw new CodeModeException("type_error", "Invalid indexed assignment.");
                }
                throw new CodeModeException("syntax_error", "Invalid assignment target.");
            }

            private object GetMember(object target, string prop, bool optional)
            {
                if (target == null || target is UndefinedValue)
                {
                    if (optional) return UndefinedValue.Instance;
                    throw new CodeModeException("type_error", $"Cannot read property '{prop}' of null/undefined.");
                }
                if (target is DynamicHostObject dyn) return dyn[prop];
                if (target is Dictionary<string, object> dict)
                    return dict.TryGetValue(prop, out object v) ? v : UndefinedValue.Instance;
                if (target is List<object> list) return GetArrayMember(list, prop);
                if (target is string str) return GetStringMember(str, prop);
                return UndefinedValue.Instance;
            }

            private object GetIndex(object target, object idx, bool optional)
            {
                if (target == null || target is UndefinedValue)
                {
                    if (optional) return UndefinedValue.Instance;
                    throw new CodeModeException("type_error", "Cannot index null/undefined.");
                }
                if (target is List<object> list)
                {
                    int i = (int)ToNumber(idx);
                    return (i >= 0 && i < list.Count) ? list[i] : UndefinedValue.Instance;
                }
                if (target is string str)
                {
                    int i = (int)ToNumber(idx);
                    return (i >= 0 && i < str.Length) ? (object)str[i].ToString() : UndefinedValue.Instance;
                }
                return GetMember(target, FormatValue(idx), optional);
            }

            private object EvalCall(CallExpr ce, Env env)
            {
                object callee = Eval(ce.Callee, env);
                var args = new List<object>(ce.Args.Count);
                foreach (Expr a in ce.Args) args.Add(Eval(a, env));
                return Invoke(callee, args);
            }

            private object Invoke(object callee, List<object> args)
            {
                if (callee is HostCallable hc) return hc(args);
                if (callee is Closure cl)
                {
                    if (_depth >= _limits.MaxCallDepth)
                        throw new CodeModeException("stack_overflow", $"Max call depth ({_limits.MaxCallDepth}) exceeded.");
                    _depth++;
                    try
                    {
                        var callEnv = new Env(cl.Captured);
                        for (int i = 0; i < cl.Params.Count; i++)
                            callEnv.Define(cl.Params[i], i < args.Count ? args[i] : UndefinedValue.Instance);
                        if (cl.ExprBody != null) return Eval(cl.ExprBody, callEnv);
                        foreach (Stmt s in cl.Body)
                        {
                            Flow f = ExecStmt(s, callEnv);
                            if (f.Kind == FlowKind.Return) return f.Value;
                        }
                        return UndefinedValue.Instance;
                    }
                    finally { _depth--; }
                }
                throw new CodeModeException("type_error", $"Value '{FormatValue(callee)}' is not callable.");
            }

            // ---- Safe Array & String methods ----------------------------------------------

            private object GetArrayMember(List<object> list, string prop)
            {
                switch (prop)
                {
                    case "length": return (double)list.Count;
                    case "push": return (HostCallable)(args => { foreach (var a in args) list.Add(a); return (double)list.Count; });
                    case "pop": return (HostCallable)(_ => { if (list.Count == 0) return UndefinedValue.Instance; var v = list[list.Count - 1]; list.RemoveAt(list.Count - 1); return v; });
                    case "join": return (HostCallable)(args => string.Join(args.Count > 0 ? FormatValue(args[0]) : ",", list.Select(x => x == null || x is UndefinedValue ? "" : FormatValue(x))));
                    case "includes": return (HostCallable)(args => args.Count > 0 && list.Any(x => LooseEquals(x, args[0])));
                    case "indexOf": return (HostCallable)(args => args.Count == 0 ? -1.0 : (double)list.FindIndex(x => StrictEquals(x, args[0])));
                    case "slice":
                        return (HostCallable)(args =>
                        {
                            int start = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
                            int end = args.Count > 1 ? (int)ToNumber(args[1]) : list.Count;
                            if (start < 0) start = Math.Max(0, list.Count + start);
                            if (end < 0) end = Math.Max(0, list.Count + end);
                            start = Math.Min(start, list.Count);
                            end = Math.Min(Math.Max(end, start), list.Count);
                            return list.GetRange(start, end - start);
                        });
                    case "concat":
                        return (HostCallable)(args =>
                        {
                            var res = new List<object>(list);
                            foreach (var a in args) { if (a is List<object> al) res.AddRange(al); else res.Add(a); }
                            return res;
                        });
                    case "flat":
                        return (HostCallable)(_ =>
                        {
                            var res = new List<object>();
                            foreach (var item in list) { if (item is List<object> al) res.AddRange(al); else res.Add(item); }
                            return res;
                        });
                    case "reverse": return (HostCallable)(_ => { list.Reverse(); return list; });
                    case "map":
                        return (HostCallable)(args =>
                        {
                            var res = new List<object>(list.Count);
                            for (int i = 0; i < list.Count; i++) { Tick(); res.Add(Invoke(args[0], new List<object> { list[i], (double)i, list })); }
                            return res;
                        });
                    case "filter":
                        return (HostCallable)(args =>
                        {
                            var res = new List<object>();
                            for (int i = 0; i < list.Count; i++) { Tick(); if (IsTruthy(Invoke(args[0], new List<object> { list[i], (double)i, list }))) res.Add(list[i]); }
                            return res;
                        });
                    case "find":
                        return (HostCallable)(args =>
                        {
                            for (int i = 0; i < list.Count; i++) { Tick(); if (IsTruthy(Invoke(args[0], new List<object> { list[i], (double)i, list }))) return list[i]; }
                            return UndefinedValue.Instance;
                        });
                    case "findIndex":
                        return (HostCallable)(args =>
                        {
                            for (int i = 0; i < list.Count; i++) { Tick(); if (IsTruthy(Invoke(args[0], new List<object> { list[i], (double)i, list }))) return (double)i; }
                            return -1.0;
                        });
                    case "some":
                        return (HostCallable)(args =>
                        {
                            for (int i = 0; i < list.Count; i++) { Tick(); if (IsTruthy(Invoke(args[0], new List<object> { list[i], (double)i, list }))) return true; }
                            return false;
                        });
                    case "every":
                        return (HostCallable)(args =>
                        {
                            for (int i = 0; i < list.Count; i++) { Tick(); if (!IsTruthy(Invoke(args[0], new List<object> { list[i], (double)i, list }))) return false; }
                            return true;
                        });
                    case "forEach":
                        return (HostCallable)(args =>
                        {
                            for (int i = 0; i < list.Count; i++) { Tick(); Invoke(args[0], new List<object> { list[i], (double)i, list }); }
                            return UndefinedValue.Instance;
                        });
                    case "reduce":
                        return (HostCallable)(args =>
                        {
                            int start = args.Count >= 2 ? 0 : 1;
                            if (list.Count == 0 && args.Count < 2) throw new CodeModeException("type_error", "Reduce of empty array with no initial value.");
                            object acc = args.Count >= 2 ? args[1] : list[0];
                            for (int i = start; i < list.Count; i++) { Tick(); acc = Invoke(args[0], new List<object> { acc, list[i], (double)i, list }); }
                            return acc;
                        });
                    case "sort":
                        return (HostCallable)(args =>
                        {
                            if (args.Count > 0 && (args[0] is Closure || args[0] is HostCallable))
                                list.Sort((a, b) => Math.Sign(ToNumber(Invoke(args[0], new List<object> { a, b }))));
                            else
                                list.Sort((a, b) => string.CompareOrdinal(FormatValue(a), FormatValue(b)));
                            return list;
                        });
                }
                return UndefinedValue.Instance;
            }

            private object GetStringMember(string s, string prop)
            {
                switch (prop)
                {
                    case "length": return (double)s.Length;
                    case "toLowerCase": return (HostCallable)(_ => s.ToLowerInvariant());
                    case "toUpperCase": return (HostCallable)(_ => s.ToUpperInvariant());
                    case "trim": return (HostCallable)(_ => s.Trim());
                    case "includes": return (HostCallable)(args => args.Count > 0 && s.Contains(FormatValue(args[0])));
                    case "startsWith": return (HostCallable)(args => args.Count > 0 && s.StartsWith(FormatValue(args[0]), StringComparison.Ordinal));
                    case "endsWith": return (HostCallable)(args => args.Count > 0 && s.EndsWith(FormatValue(args[0]), StringComparison.Ordinal));
                    case "indexOf": return (HostCallable)(args => args.Count == 0 ? -1.0 : (double)s.IndexOf(FormatValue(args[0]), StringComparison.Ordinal));
                    case "split":
                        return (HostCallable)(args =>
                        {
                            if (args.Count == 0) return new List<object> { s };
                            string sep = FormatValue(args[0]);
                            if (sep.Length == 0) return s.Select(c => (object)c.ToString()).ToList();
                            return s.Split(new[] { sep }, StringSplitOptions.None).Cast<object>().ToList();
                        });
                    case "replace":
                        return (HostCallable)(args =>
                        {
                            if (args.Count < 2) return s;
                            string find = FormatValue(args[0]), rep = FormatValue(args[1]);
                            int idx = s.IndexOf(find, StringComparison.Ordinal);
                            return idx < 0 ? s : s.Substring(0, idx) + rep + s.Substring(idx + find.Length);
                        });
                    case "replaceAll":
                        return (HostCallable)(args => args.Count < 2 ? s : s.Replace(FormatValue(args[0]), FormatValue(args[1])));
                    case "slice":
                    case "substring":
                        return (HostCallable)(args =>
                        {
                            int start = args.Count > 0 ? (int)ToNumber(args[0]) : 0;
                            int end = args.Count > 1 ? (int)ToNumber(args[1]) : s.Length;
                            if (prop == "slice")
                            {
                                if (start < 0) start = Math.Max(0, s.Length + start);
                                if (end < 0) end = Math.Max(0, s.Length + end);
                            }
                            start = Math.Max(0, Math.Min(start, s.Length));
                            end = Math.Max(start, Math.Min(end, s.Length));
                            return s.Substring(start, end - start);
                        });
                }
                return UndefinedValue.Instance;
            }
        }
    }
}

