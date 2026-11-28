using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Revit.Sdk
{
    /// <summary>
    /// Captures and suppresses non-fatal Revit warnings during automated transactions so modal
    /// warning dialogs (e.g. "identical instances in the same place") never block the Revit UI thread.
    /// </summary>
    internal sealed class ScioWarningCapturePreprocessor : IFailuresPreprocessor
    {
        public List<string> CapturedWarnings { get; } = new List<string>();
        public List<string> CapturedErrors { get; } = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
            {
                string desc = failure.GetDescriptionText();
                FailureSeverity severity = failure.GetSeverity();
                if (severity == FailureSeverity.Warning)
                {
                    if (!string.IsNullOrWhiteSpace(desc)) CapturedWarnings.Add(desc);
                    failuresAccessor.DeleteWarning(failure);
                }
                else if (severity == FailureSeverity.Error)
                {
                    if (!string.IsNullOrWhiteSpace(desc)) CapturedErrors.Add(desc);
                }
            }
            return FailureProcessingResult.Continue;
        }
    }

    /// <summary>
    /// Unified Revit transaction runner enforcing dry-run rollback, warning capture, and post-commit re-read verification.
    /// </summary>
    public static class TransactionPipeline
    {
        /// <summary>
        /// Attaches Sutra's non-fatal warning capture preprocessor to a raw Revit Transaction
        /// so modal warning dialogs never block the UI thread.
        /// </summary>
        public static List<string> AttachWarningCapture(Transaction tx)
        {
            var capture = new ScioWarningCapturePreprocessor();
            FailureHandlingOptions fho = tx.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(capture);
            fho.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(fho);
            return capture.CapturedWarnings;
        }

        public static ToolResponse RunVerified(
            Document doc,
            string requestId,
            string transactionName,
            bool dryRun,
            Func<Document, JObject> mutate,
            Func<Document, JObject, JObject> verifyAfterCommit,
            string summaryPrefix)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (mutate == null) throw new ArgumentNullException(nameof(mutate));
            if (verifyAfterCommit == null) throw new ArgumentNullException(nameof(verifyAfterCommit));

            using (var tx = new Transaction(doc, $"SCIO: {transactionName}"))
            {
                var failureCapture = new ScioWarningCapturePreprocessor();
                FailureHandlingOptions fho = tx.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(failureCapture);
                fho.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(fho);

                tx.Start();
                JObject staged;
                try
                {
                    staged = mutate(doc);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return ToolResponse.Failure(requestId, "transaction_failed", ex.Message);
                }

                if (dryRun)
                {
                    tx.RollBack();
                    staged["dry_run"] = true;
                    if (failureCapture.CapturedWarnings.Count > 0)
                        staged["captured_warnings"] = new JArray(failureCapture.CapturedWarnings);
                    return ToolResponse.Success(
                        requestId,
                        staged,
                        $"[DRY RUN] {summaryPrefix}",
                        VerificationStatus.DryRunPreview,
                        hostVerified: false);
                }

                TransactionStatus status = tx.Commit();
                if (status != TransactionStatus.Committed)
                {
                    string errDetail = failureCapture.CapturedErrors.Count > 0
                        ? " Errors: " + string.Join(" | ", failureCapture.CapturedErrors)
                        : string.Empty;
                    return ToolResponse.Failure(
                        requestId,
                        "commit_failed",
                        $"Revit transaction '{transactionName}' ended with status {status}.{errDetail}");
                }

                JObject verified = verifyAfterCommit(doc, staged);
                verified["dry_run"] = false;
                if (failureCapture.CapturedWarnings.Count > 0)
                    verified["captured_warnings"] = new JArray(failureCapture.CapturedWarnings);
                return ToolResponse.Success(
                    requestId,
                    verified,
                    summaryPrefix,
                    VerificationStatus.Verified,
                    hostVerified: true);
            }
        }
    }
}
