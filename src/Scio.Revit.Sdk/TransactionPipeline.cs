using System;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Revit.Sdk
{
    /// <summary>
    /// Unified Revit transaction runner enforcing dry-run rollback and post-commit re-read verification.
    /// </summary>
    public static class TransactionPipeline
    {
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
                    return ToolResponse.Failure(
                        requestId,
                        "commit_failed",
                        $"Revit transaction '{transactionName}' ended with status {status}.");
                }

                JObject verified = verifyAfterCommit(doc, staged);
                verified["dry_run"] = false;
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
