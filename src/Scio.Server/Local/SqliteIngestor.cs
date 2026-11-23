#nullable disable
// -----------------------------------------------------------------------------
// SCIO Revit MCP - Local SQLite Ingestion & Read-Only Query Service
//
// Ingests CSV and structured JSON outputs into %USERPROFILE%\.scio\scio_local.db
// using Windows built-in winsqlite3.dll and emits lightweight .ids.json
// sidecars for Revit-side @run:<id> resolution. Operates strictly in Scio.Server.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    public static class SqliteIngestor
    {
        private static readonly object SyncLock = new object();
        private static string _initializedDbPath;

        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;
        private const int SQLITE_OPEN_READONLY = 0x00000001;
        private const int SQLITE_OPEN_READWRITE = 0x00000002;
        private const int SQLITE_OPEN_CREATE = 0x00000004;

        private const int SQLITE_INTEGER = 1;
        private const int SQLITE_FLOAT = 2;
        private const int SQLITE_NULL = 5;

        private static readonly HashSet<string> IngestibleTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "scio_censo",
            "scio_metrado_partidas",
            "scio_auditoria_parametros",
            "scio_naming_check",
            "scio_clash",
            "scio_audit_model",
            "scio_get_schedule_data",
            "scio_quantities",
            "scio_query_model",
            "scio_presync_check"
        };

        private static readonly Regex ForbiddenSqlKeywords = new Regex(
            @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|REPLACE|PRAGMA|ATTACH|DETACH|VACUUM|REINDEX|TRIGGER|EXEC)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close_v2(IntPtr db);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr ptr);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, out IntPtr pzTail);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_count", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr stmt);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_name", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_name(IntPtr stmt, int n);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_double", CallingConvention = CallingConvention.Cdecl)]
        private static extern double sqlite3_column_double(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        public static string GetDatabasePath()
        {
            string scioDir = ScioPaths.DataRoot();
            if (!Directory.Exists(scioDir))
            {
                try { Directory.CreateDirectory(scioDir); } catch { }
            }
            return Path.Combine(scioDir, "scio_local.db");
        }

        public static bool ShouldIngest(string toolName) =>
            !string.IsNullOrWhiteSpace(toolName) && IngestibleTools.Contains(toolName);

        private static void EnsureSchema(IntPtr db, string dbPath)
        {
            if (string.Equals(_initializedDbPath, dbPath, StringComparison.OrdinalIgnoreCase)) return;

            string schemaSql = @"
                CREATE TABLE IF NOT EXISTS runs (
                    run_id TEXT PRIMARY KEY,
                    command TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    model_name TEXT,
                    file_path TEXT NOT NULL,
                    total_rows INTEGER NOT NULL,
                    summary_json TEXT
                );
                CREATE TABLE IF NOT EXISTS censo_elements (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id TEXT NOT NULL REFERENCES runs(run_id),
                    documento TEXT,
                    origen TEXT,
                    element_id INTEGER,
                    unique_id TEXT,
                    categoria TEXT,
                    familia TEXT,
                    tipo TEXT,
                    nivel TEXT
                );
                CREATE TABLE IF NOT EXISTS metrados (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id TEXT NOT NULL REFERENCES runs(run_id),
                    documento TEXT,
                    origen TEXT,
                    element_id INTEGER,
                    unique_id TEXT,
                    partida TEXT,
                    categoria TEXT,
                    familia TEXT,
                    tipo TEXT,
                    nivel TEXT,
                    volumen_m3 REAL DEFAULT 0,
                    area_m2 REAL DEFAULT 0,
                    longitud_m REAL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS auditoria_parametros (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id TEXT NOT NULL REFERENCES runs(run_id),
                    documento TEXT,
                    origen TEXT,
                    element_id INTEGER,
                    unique_id TEXT,
                    categoria TEXT,
                    familia TEXT,
                    tipo TEXT,
                    nivel TEXT,
                    parametro TEXT,
                    estado TEXT,
                    valor_actual TEXT
                );
                CREATE TABLE IF NOT EXISTS clashes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id TEXT NOT NULL REFERENCES runs(run_id),
                    clash_index INTEGER,
                    model_a TEXT,
                    element_a_id INTEGER,
                    category_a TEXT,
                    model_b TEXT,
                    element_b_id INTEGER,
                    category_b TEXT,
                    volume_m3 REAL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS audit_findings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id TEXT NOT NULL REFERENCES runs(run_id),
                    finding_id TEXT,
                    check_name TEXT,
                    severity TEXT,
                    count INTEGER DEFAULT 0,
                    element_ids_json TEXT,
                    summary TEXT
                );
                CREATE TABLE IF NOT EXISTS schedule_rows (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id TEXT NOT NULL REFERENCES runs(run_id),
                    schedule_id INTEGER,
                    schedule_name TEXT,
                    row_index INTEGER,
                    row_json TEXT
                );
                CREATE TABLE IF NOT EXISTS query_rows (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id TEXT NOT NULL REFERENCES runs(run_id),
                    element_id INTEGER,
                    category TEXT,
                    family TEXT,
                    type TEXT,
                    level TEXT,
                    source_model TEXT,
                    data_json TEXT
                );
                CREATE TABLE IF NOT EXISTS naming_results (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id TEXT NOT NULL REFERENCES runs(run_id),
                    scope TEXT,
                    element_id INTEGER,
                    name TEXT,
                    tested_string TEXT,
                    status TEXT,
                    suggested_fix TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_runs_cmd_ts ON runs(command, timestamp_utc);
                CREATE INDEX IF NOT EXISTS idx_censo_run ON censo_elements(run_id);
                CREATE INDEX IF NOT EXISTS idx_censo_category ON censo_elements(categoria);
                CREATE INDEX IF NOT EXISTS idx_metrados_run ON metrados(run_id);
                CREATE INDEX IF NOT EXISTS idx_metrados_partida ON metrados(partida);
                CREATE INDEX IF NOT EXISTS idx_auditoria_run ON auditoria_parametros(run_id);
                CREATE INDEX IF NOT EXISTS idx_clashes_run ON clashes(run_id);
                CREATE INDEX IF NOT EXISTS idx_audit_findings_run ON audit_findings(run_id);
                CREATE INDEX IF NOT EXISTS idx_schedule_rows_run ON schedule_rows(run_id);
                CREATE INDEX IF NOT EXISTS idx_query_rows_run ON query_rows(run_id);
                CREATE INDEX IF NOT EXISTS idx_naming_results_run ON naming_results(run_id);
            ";

            ExecuteSql(db, schemaSql);
            _initializedDbPath = dbPath;
        }

        public static void TryIngestAsync(string toolName, JToken result, string modelName = null)
        {
            if (!ShouldIngest(toolName) || result == null) return;

            Task.Run(() =>
            {
                try { TryIngestResultSync(toolName, result, modelName); }
                catch { }
            });
        }

        public static string TryIngestResultSync(string toolName, JToken result, string modelName = null)
        {
            if (string.IsNullOrWhiteSpace(toolName) || result == null) return null;

            try
            {
                JObject payload = ExtractPayloadObject(result);
                string resolvedModel = modelName ?? ExtractModelName(payload);
                string csvPath = ExtractFilePath(result);

                if (!string.IsNullOrWhiteSpace(csvPath) && File.Exists(csvPath))
                {
                    return IngestFileInternal(toolName, csvPath, payload ?? result, resolvedModel);
                }

                if (payload != null)
                {
                    return IngestJsonPayload(toolName, payload, resolvedModel);
                }
            }
            catch { }

            return null;
        }

        public static void IngestFile(string toolName, string filePath, JToken result, string modelName = null)
        {
            IngestFileInternal(toolName, filePath, result, modelName);
        }

        public static string IngestFileInternal(string toolName, string filePath, JToken result, string modelName = null)
        {
            lock (SyncLock)
            {
                string dbPath = GetDatabasePath();
                byte[] dbPathUtf8 = Encoding.UTF8.GetBytes(dbPath + "\0");

                int rc = sqlite3_open_v2(dbPathUtf8, out IntPtr db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, IntPtr.Zero);
                if (rc != SQLITE_OK || db == IntPtr.Zero) return null;

                try
                {
                    EnsureSchema(db, dbPath);

                    string runId = NewRunId();
                    string nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    string summaryJson = CompactSummaryForRun(result);

                    var lines = ReadCsvLines(filePath);
                    if (lines.Count <= 1) return null;

                    var elemIds = new List<long>();
                    var sb = new StringBuilder();
                    sb.AppendLine("BEGIN TRANSACTION;");

                    sb.AppendLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "INSERT INTO runs (run_id, command, timestamp_utc, model_name, file_path, total_rows, summary_json) " +
                        "VALUES ('{0}', '{1}', '{2}', '{3}', '{4}', {5}, '{6}');",
                        Escape(runId),
                        Escape(toolName),
                        Escape(nowUtc),
                        Escape(modelName ?? string.Empty),
                        Escape(filePath),
                        lines.Count - 1,
                        Escape(summaryJson)));

                    for (int i = 1; i < lines.Count; i++)
                    {
                        var row = lines[i];
                        if (row == null || row.Count == 0) continue;

                        if (toolName == "scio_censo" && row.Count >= 8)
                        {
                            long.TryParse(row[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long elemId);
                            if (elemId > 0) elemIds.Add(elemId);
                            sb.AppendLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "INSERT INTO censo_elements (run_id, documento, origen, element_id, unique_id, categoria, familia, tipo, nivel) " +
                                "VALUES ('{0}', '{1}', '{2}', {3}, '{4}', '{5}', '{6}', '{7}', '{8}');",
                                Escape(runId), Escape(row[0]), Escape(row[1]), elemId, Escape(row[3]),
                                Escape(row[4]), Escape(row[5]), Escape(row[6]), Escape(row[7])));
                        }
                        else if (toolName == "scio_metrado_partidas" && row.Count >= 12)
                        {
                            long.TryParse(row[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long elemId);
                            if (elemId > 0) elemIds.Add(elemId);
                            double.TryParse(row[9], NumberStyles.Any, CultureInfo.InvariantCulture, out double vol);
                            double.TryParse(row[10], NumberStyles.Any, CultureInfo.InvariantCulture, out double area);
                            double.TryParse(row[11], NumberStyles.Any, CultureInfo.InvariantCulture, out double len);

                            sb.AppendLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "INSERT INTO metrados (run_id, documento, origen, element_id, unique_id, partida, categoria, familia, tipo, nivel, volumen_m3, area_m2, longitud_m) " +
                                "VALUES ('{0}', '{1}', '{2}', {3}, '{4}', '{5}', '{6}', '{7}', '{8}', '{9}', {10}, {11}, {12});",
                                Escape(runId), Escape(row[0]), Escape(row[1]), elemId, Escape(row[3]),
                                Escape(row[4]), Escape(row[5]), Escape(row[6]), Escape(row[7]), Escape(row[8]),
                                vol, area, len));
                        }
                        else if (toolName == "scio_auditoria_parametros" && row.Count >= 11)
                        {
                            long.TryParse(row[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long elemId);
                            if (elemId > 0) elemIds.Add(elemId);
                            sb.AppendLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "INSERT INTO auditoria_parametros (run_id, documento, origen, element_id, unique_id, categoria, familia, tipo, nivel, parametro, estado, valor_actual) " +
                                "VALUES ('{0}', '{1}', '{2}', {3}, '{4}', '{5}', '{6}', '{7}', '{8}', '{9}', '{10}', '{11}');",
                                Escape(runId), Escape(row[0]), Escape(row[1]), elemId, Escape(row[3]),
                                Escape(row[4]), Escape(row[5]), Escape(row[6]), Escape(row[7]),
                                Escape(row[8]), Escape(row[9]), Escape(row[10])));
                        }
                        else if (toolName == "scio_naming_check" && row.Count >= 7)
                        {
                            long.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long elemId);
                            if (elemId > 0 && !string.Equals(row[5], "CONFORME", StringComparison.OrdinalIgnoreCase))
                                elemIds.Add(elemId);
                            sb.AppendLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "INSERT INTO naming_results (run_id, scope, element_id, name, tested_string, status, suggested_fix) " +
                                "VALUES ('{0}', '{1}', {2}, '{3}', '{4}', '{5}', '{6}');",
                                Escape(runId), Escape(row[0]), elemId, Escape(row[2]),
                                Escape(row[3]), Escape(row[5]), Escape(row[6])));
                        }
                        else if (toolName == "scio_query_model")
                        {
                            // Generic query CSV written by QueryModelCommand export_csv
                            long elemId = 0;
                            if (lines[0].Contains("ElementId"))
                            {
                                int idx = lines[0].IndexOf("ElementId");
                                if (idx >= 0 && idx < row.Count)
                                    long.TryParse(row[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out elemId);
                            }
                            if (elemId > 0) elemIds.Add(elemId);
                            sb.AppendLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "INSERT INTO query_rows (run_id, element_id, category, family, type, level, source_model, data_json) " +
                                "VALUES ('{0}', {1}, '{2}', '{3}', '{4}', '{5}', '{6}', '{7}');",
                                Escape(runId), elemId,
                                Escape(SafeCol(row, 1)), Escape(SafeCol(row, 2)),
                                Escape(SafeCol(row, 3)), Escape(SafeCol(row, 4)),
                                Escape(modelName ?? string.Empty),
                                Escape(new JArray(row).ToString(Formatting.None))));
                        }
                    }

                    sb.AppendLine("COMMIT;");
                    if (!ExecuteSql(db, sb.ToString())) return null;

                    RunRefResolver.WriteIdsSidecar(runId, toolName, modelName, elemIds);
                    return runId;
                }
                finally
                {
                    sqlite3_close_v2(db);
                }
            }
        }

        private static string IngestJsonPayload(string toolName, JObject payload, string modelName)
        {
            lock (SyncLock)
            {
                string dbPath = GetDatabasePath();
                byte[] dbPathUtf8 = Encoding.UTF8.GetBytes(dbPath + "\0");
                int rc = sqlite3_open_v2(dbPathUtf8, out IntPtr db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, IntPtr.Zero);
                if (rc != SQLITE_OK || db == IntPtr.Zero) return null;

                try
                {
                    EnsureSchema(db, dbPath);
                    string runId = NewRunId();
                    string nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    var elemIds = new List<long>();
                    var sb = new StringBuilder();
                    int totalRows = 0;

                    sb.AppendLine("BEGIN TRANSACTION;");

                    if (toolName == "scio_clash" && payload["clashes"] is JArray clashes)
                    {
                        foreach (JObject c in clashes.OfType<JObject>())
                        {
                            totalRows++;
                            int cIdx = c.Value<int?>("clash_index") ?? totalRows - 1;
                            JObject a = c["element_a"] as JObject ?? c["a"] as JObject;
                            JObject b = c["element_b"] as JObject ?? c["b"] as JObject;
                            long idA = a?.Value<long?>("id") ?? a?.Value<long?>("element_id") ?? c.Value<long?>("element_a_id") ?? 0;
                            long idB = b?.Value<long?>("id") ?? b?.Value<long?>("element_id") ?? c.Value<long?>("element_b_id") ?? 0;
                            if (idA > 0) elemIds.Add(idA);
                            if (idB > 0) elemIds.Add(idB);
                            string modA = a?.Value<string>("source") ?? c.Value<string>("model_a") ?? "host";
                            string modB = b?.Value<string>("source") ?? c.Value<string>("model_b") ?? "host";
                            string catA = a?.Value<string>("category") ?? c.Value<string>("category_a") ?? string.Empty;
                            string catB = b?.Value<string>("category") ?? c.Value<string>("category_b") ?? string.Empty;
                            double vol = c.Value<double?>("intersection_volume_m3") ?? 0.0;

                            sb.AppendLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "INSERT INTO clashes (run_id, clash_index, model_a, element_a_id, category_a, model_b, element_b_id, category_b, volume_m3) " +
                                "VALUES ('{0}', {1}, '{2}', {3}, '{4}', '{5}', {6}, '{7}', {8});",
                                Escape(runId), cIdx, Escape(modA), idA, Escape(catA), Escape(modB), idB, Escape(catB), vol));
                        }
                    }
                    else if (toolName == "scio_audit_model" && payload["findings"] is JArray findings)
                    {
                        foreach (JObject f in findings.OfType<JObject>())
                        {
                            totalRows++;
                            string fId = f.Value<string>("finding_id") ?? string.Empty;
                            string check = f.Value<string>("check") ?? string.Empty;
                            string sev = f.Value<string>("severity") ?? f.Value<string>("level") ?? string.Empty;
                            int cnt = f.Value<int?>("count") ?? 0;
                            var ids = f["element_ids"] as JArray ?? new JArray();
                            foreach (JToken t in ids)
                            {
                                if (long.TryParse(t.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v > 0)
                                    elemIds.Add(v);
                            }
                            sb.AppendLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "INSERT INTO audit_findings (run_id, finding_id, check_name, severity, count, element_ids_json, summary) " +
                                "VALUES ('{0}', '{1}', '{2}', '{3}', {4}, '{5}', '{6}');",
                                Escape(runId), Escape(fId), Escape(check), Escape(sev), cnt,
                                Escape(ids.ToString(Formatting.None)),
                                Escape(f.Value<string>("message") ?? f.Value<string>("summary") ?? string.Empty)));
                        }
                    }
                    else if (toolName == "scio_get_schedule_data" && payload["body"] is JArray body)
                    {
                        long schedId = payload.Value<long?>("schedule_id") ?? 0;
                        string schedName = payload.Value<string>("name") ?? string.Empty;
                        for (int r = 0; r < body.Count; r++)
                        {
                            totalRows++;
                            sb.AppendLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "INSERT INTO schedule_rows (run_id, schedule_id, schedule_name, row_index, row_json) " +
                                "VALUES ('{0}', {1}, '{2}', {3}, '{4}');",
                                Escape(runId), schedId, Escape(schedName), r, Escape(body[r].ToString(Formatting.None))));
                        }
                    }
                    else if (toolName == "scio_query_model" && payload["rows"] is JArray qRows)
                    {
                        foreach (JObject r in qRows.OfType<JObject>())
                        {
                            totalRows++;
                            long eId = r.Value<long?>("id") ?? r.Value<long?>("element_id") ?? 0;
                            if (eId > 0) elemIds.Add(eId);
                            sb.AppendLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "INSERT INTO query_rows (run_id, element_id, category, family, type, level, source_model, data_json) " +
                                "VALUES ('{0}', {1}, '{2}', '{3}', '{4}', '{5}', '{6}', '{7}');",
                                Escape(runId), eId,
                                Escape(r.Value<string>("category") ?? string.Empty),
                                Escape(r.Value<string>("family") ?? string.Empty),
                                Escape(r.Value<string>("type") ?? string.Empty),
                                Escape(r.Value<string>("level") ?? string.Empty),
                                Escape(r.Value<string>("source_model") ?? modelName ?? string.Empty),
                                Escape(r.ToString(Formatting.None))));
                        }
                    }
                    else if (toolName == "scio_quantities" && payload["rows"] is JArray qtyRows)
                    {
                        foreach (JObject r in qtyRows.OfType<JObject>())
                        {
                            totalRows++;
                            long eId = r.Value<long?>("element_id") ?? r.Value<long?>("id") ?? 0;
                            if (eId > 0) elemIds.Add(eId);
                            sb.AppendLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "INSERT INTO metrados (run_id, documento, origen, element_id, unique_id, partida, categoria, familia, tipo, nivel, volumen_m3, area_m2, longitud_m) " +
                                "VALUES ('{0}', '{1}', '{2}', {3}, '', '{4}', '{5}', '', '{6}', '', {7}, {8}, {9});",
                                Escape(runId),
                                Escape(modelName ?? string.Empty),
                                Escape(r.Value<string>("source") ?? "host"),
                                eId,
                                Escape(r.Value<string>("code") ?? string.Empty),
                                Escape(r.Value<string>("category") ?? string.Empty),
                                Escape(r.Value<string>("type") ?? string.Empty),
                                r.Value<double?>("geometry_m3") ?? r.Value<double?>("parameter_m3") ?? 0.0,
                                r.Value<double?>("area_m2") ?? 0.0,
                                r.Value<double?>("length_m") ?? 0.0));
                        }
                    }
                    else if (toolName == "scio_presync_check")
                    {
                        totalRows = 1;
                    }
                    else
                    {
                        return null;
                    }

                    if (totalRows == 0 && toolName != "scio_presync_check" && toolName != "scio_clash" && toolName != "scio_audit_model")
                        return null;

                    string insertRun = string.Format(
                        CultureInfo.InvariantCulture,
                        "INSERT INTO runs (run_id, command, timestamp_utc, model_name, file_path, total_rows, summary_json) " +
                        "VALUES ('{0}', '{1}', '{2}', '{3}', '', {4}, '{5}');",
                        Escape(runId), Escape(toolName), Escape(nowUtc),
                        Escape(modelName ?? string.Empty), totalRows, Escape(CompactSummaryForRun(payload)));

                    string fullSql = "BEGIN TRANSACTION;\n" + insertRun + "\n" +
                                     sb.ToString().Replace("BEGIN TRANSACTION;", "") + "\nCOMMIT;";
                    if (!ExecuteSql(db, fullSql)) return null;

                    RunRefResolver.WriteIdsSidecar(runId, toolName, modelName, elemIds);
                    return runId;
                }
                finally
                {
                    sqlite3_close_v2(db);
                }
            }
        }

        public static JObject QueryReadOnly(string sql, int maxRows = 50)
        {
            string problem = ValidateReadOnlySql(sql);
            if (problem != null)
                return new JObject { ["ok"] = false, ["error"] = problem };

            int bounded = Math.Max(1, Math.Min(500, maxRows));
            lock (SyncLock)
            {
                string dbPath = GetDatabasePath();
                if (!File.Exists(dbPath))
                {
                    return new JObject
                    {
                        ["ok"] = true,
                        ["columns"] = new JArray(),
                        ["rows"] = new JArray(),
                        ["row_count"] = 0,
                        ["truncated"] = false,
                        ["note"] = "scio_local.db does not exist yet (no runs ingested)."
                    };
                }

                byte[] dbPathUtf8 = Encoding.UTF8.GetBytes(dbPath + "\0");
                int rc = sqlite3_open_v2(dbPathUtf8, out IntPtr db, SQLITE_OPEN_READONLY, IntPtr.Zero);
                if (rc != SQLITE_OK || db == IntPtr.Zero)
                    return new JObject { ["ok"] = false, ["error"] = "Failed to open scio_local.db read-only." };

                IntPtr stmt = IntPtr.Zero;
                try
                {
                    byte[] sqlBytes = Encoding.UTF8.GetBytes(sql.Trim().TrimEnd(';') + "\0");
                    int prepRc = sqlite3_prepare_v2(db, sqlBytes, sqlBytes.Length - 1, out stmt, out _);
                    if (prepRc != SQLITE_OK || stmt == IntPtr.Zero)
                    {
                        string msg = ReadUtf8(sqlite3_errmsg(db)) ?? ("SQLite error code " + prepRc);
                        return new JObject { ["ok"] = false, ["error"] = "SQL query failed: " + msg };
                    }

                    int colCount = sqlite3_column_count(stmt);
                    var columns = new JArray();
                    for (int i = 0; i < colCount; i++)
                        columns.Add(ReadUtf8(sqlite3_column_name(stmt, i)) ?? ("col_" + i));

                    var rows = new JArray();
                    bool truncated = false;
                    while (true)
                    {
                        int stepRc = sqlite3_step(stmt);
                        if (stepRc == SQLITE_DONE) break;
                        if (stepRc != SQLITE_ROW)
                        {
                            string msg = ReadUtf8(sqlite3_errmsg(db)) ?? ("SQLite step error " + stepRc);
                            return new JObject { ["ok"] = false, ["error"] = "SQL execution failed: " + msg };
                        }
                        if (rows.Count >= bounded)
                        {
                            truncated = true;
                            break;
                        }

                        var rowObj = new JObject();
                        for (int c = 0; c < colCount; c++)
                        {
                            string colName = (string)columns[c];
                            int ctype = sqlite3_column_type(stmt, c);
                            switch (ctype)
                            {
                                case SQLITE_NULL:
                                    rowObj[colName] = JValue.CreateNull();
                                    break;
                                case SQLITE_INTEGER:
                                    rowObj[colName] = sqlite3_column_int64(stmt, c);
                                    break;
                                case SQLITE_FLOAT:
                                    rowObj[colName] = sqlite3_column_double(stmt, c);
                                    break;
                                default:
                                    rowObj[colName] = ReadUtf8(sqlite3_column_text(stmt, c)) ?? string.Empty;
                                    break;
                            }
                        }
                        rows.Add(rowObj);
                    }

                    return new JObject
                    {
                        ["ok"] = true,
                        ["columns"] = columns,
                        ["rows"] = rows,
                        ["row_count"] = rows.Count,
                        ["truncated"] = truncated
                    };
                }
                finally
                {
                    if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                    sqlite3_close_v2(db);
                }
            }
        }

        public static string ValidateReadOnlySql(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return "sql is required.";

            string trimmed = sql.Trim();
            if (trimmed.EndsWith(";", StringComparison.Ordinal))
                trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();

            // Forbid multi-statements / comments that hide statements.
            if (trimmed.Contains(";"))
                return "Only a single SELECT statement is allowed (multiple statements separated by ';' are forbidden).";
            if (trimmed.Contains("--") || trimmed.Contains("/*"))
                return "SQL comments (-- or /*) are forbidden in read-only local queries.";

            if (!trimmed.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("WITH ", StringComparison.OrdinalIgnoreCase))
            {
                return "Only SELECT or WITH ... SELECT read-only queries are allowed.";
            }

            if (ForbiddenSqlKeywords.IsMatch(trimmed))
                return "Write, DDL, PRAGMA, and ATTACH keywords are strictly forbidden in read-only queries.";

            return null;
        }

        public static JObject ListRuns(string command = null, string modelName = null, int limit = 20)
        {
            int cap = Math.Max(1, Math.Min(100, limit));
            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(command))
                filters.Add("command = '" + Escape(command.Trim()) + "'");
            if (!string.IsNullOrWhiteSpace(modelName))
                filters.Add("LOWER(model_name) LIKE '%" + Escape(modelName.Trim().ToLowerInvariant()) + "%'");

            string where = filters.Count > 0 ? " WHERE " + string.Join(" AND ", filters) : string.Empty;
            string sql = $"SELECT run_id, command, timestamp_utc, model_name, file_path, total_rows FROM runs{where} ORDER BY timestamp_utc DESC LIMIT {cap}";
            return QueryReadOnly(sql, cap);
        }

        public static JObject GetSchema()
        {
            return QueryReadOnly("SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", 50);
        }

        public static List<JObject> LoadRowsAsObjects(string runId, int maxRows = 5000)
        {
            var result = new List<JObject>();
            string safeId = RunRefResolver.SanitizeId(runId);
            if (safeId.Length == 0) return result;

            JObject runLookup = QueryReadOnly($"SELECT command FROM runs WHERE run_id = '{Escape(safeId)}' LIMIT 1", 1);
            if (runLookup.Value<bool?>("ok") != true) return result;
            var runRows = runLookup["rows"] as JArray;
            if (runRows == null || runRows.Count == 0) return result;

            string cmd = runRows[0].Value<string>("command");
            string sql;
            switch (cmd)
            {
                case "scio_censo":
                    sql = $"SELECT element_id, unique_id, documento, origen, categoria, familia, tipo, nivel FROM censo_elements WHERE run_id = '{Escape(safeId)}'";
                    break;
                case "scio_metrado_partidas":
                case "scio_quantities":
                    sql = $"SELECT element_id, partida, categoria, familia, tipo, nivel, volumen_m3, area_m2, longitud_m FROM metrados WHERE run_id = '{Escape(safeId)}'";
                    break;
                case "scio_auditoria_parametros":
                    sql = $"SELECT element_id, categoria, familia, tipo, nivel, parametro, estado, valor_actual FROM auditoria_parametros WHERE run_id = '{Escape(safeId)}'";
                    break;
                case "scio_clash":
                    sql = $"SELECT clash_index, model_a, element_a_id, category_a, model_b, element_b_id, category_b, volume_m3 FROM clashes WHERE run_id = '{Escape(safeId)}'";
                    break;
                case "scio_naming_check":
                    sql = $"SELECT scope, element_id, name, tested_string, status, suggested_fix FROM naming_results WHERE run_id = '{Escape(safeId)}'";
                    break;
                default:
                    sql = $"SELECT element_id, category, family, type, level, source_model FROM query_rows WHERE run_id = '{Escape(safeId)}'";
                    break;
            }

            JObject qr = QueryReadOnly(sql, maxRows);
            if (qr.Value<bool?>("ok") == true && qr["rows"] is JArray arr)
            {
                foreach (JObject obj in arr.OfType<JObject>()) result.Add(obj);
            }
            return result;
        }

        private static JObject ExtractPayloadObject(JToken result)
        {
            if (result == null) return null;
            if (result["structuredContent"] is JObject sc) return sc;
            if (result["data"] is JObject data) return data;
            return result as JObject;
        }

        private static string ExtractModelName(JObject payload)
        {
            if (payload == null) return null;
            return payload.Value<string>("documento")
                ?? payload.Value<string>("document")
                ?? payload.Value<string>("model_name")
                ?? payload["active_document"]?.Value<string>("title");
        }

        private static string CompactSummaryForRun(JToken result)
        {
            if (!(result is JObject o)) return "{}";
            var compact = new JObject();
            foreach (string k in new[] { "resumen", "summary", "cifras", "verdict", "status", "total", "total_checked", "compliance_ratio", "clash_count", "archivo_csv" })
            {
                if (o[k] != null && o[k].Type != JTokenType.Null)
                    compact[k] = o[k];
            }
            return compact.Count > 0 ? compact.ToString(Formatting.None) : "{}";
        }

        private static string SafeCol(List<string> row, int index)
            => index >= 0 && index < row.Count ? row[index] ?? string.Empty : string.Empty;

        private static string NewRunId()
            => DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private static string ReadUtf8(IntPtr ptr)
            => ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);

        private static bool IsNonCsvArtifact(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        internal static string ExtractFilePath(JToken result)
        {
            if (result == null) return null;

            foreach (JToken scope in new[] { result, result["data"], result["structuredContent"] })
            {
                if (scope == null || scope.Type != JTokenType.Object) continue;
                foreach (string key in new[] { "archivo_csv", "csv_path", "archivo" })
                {
                    string path = scope[key]?.ToString();
                    if (!string.IsNullOrWhiteSpace(path) && !IsNonCsvArtifact(path))
                        return path;
                }
            }

            return null;
        }

        private static bool ExecuteSql(IntPtr db, string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return true;
            byte[] sqlUtf8 = Encoding.UTF8.GetBytes(sql + "\0");
            int rc = sqlite3_exec(db, sqlUtf8, IntPtr.Zero, IntPtr.Zero, out IntPtr errmsg);
            if (errmsg != IntPtr.Zero)
            {
                sqlite3_free(errmsg);
            }
            return rc == SQLITE_OK;
        }

        private static string Escape(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("'", "''");
        }

        public static List<List<string>> ReadCsvLines(string filePath)
        {
            var rows = new List<List<string>>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return rows;

            using (var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.TrimStart('\uFEFF');
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                    rows.Add(ParseCsvRow(line));
                }
            }
            return rows;
        }

        private static List<string> ParseCsvRow(string line)
        {
            var result = new List<string>();
            var cur = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '\"')
                    {
                        cur.Append('\"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
            result.Add(cur.ToString());
            return result;
        }
    }
}

