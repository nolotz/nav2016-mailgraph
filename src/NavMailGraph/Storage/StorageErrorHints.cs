using System;
using System.Collections.Generic;

namespace NavMailGraph.Storage
{
    /// <summary>
    /// Configuration hints for object storage errors (appended as " Hint: ..." via <see cref="Internal.ErrorHints.Append"/>).
    /// Codes: Azure Blob error codes / x-ms-error-code, S3 &lt;Error&gt;&lt;Code&gt; (docs/research-notes-v2.md).
    /// </summary>
    internal static class StorageErrorHints
    {
        internal const string AzureAuthenticationHint =
            "Azure rejected the signature: check the storage account name and key (copy key1 or key2 exactly) and the NAV server clock "
            + "(requests must be within 15 minutes of Azure time). For a SAS URL check its expiry (se) and permissions (sp).";

        internal const string S3SignatureHint =
            "The S3 server rejected the signature or the access key: check access key id, secret access key and region "
            + "(and the S3 path-style setting for non-AWS endpoints).";

        internal const string ClockSkewHint =
            "The NAV server clock differs from the storage server by more than 15 minutes: synchronise the NAV server clock (NTP/time service).";

        internal const string AccessDeniedHint =
            "The credentials are valid but not allowed to do this: S3 – check the IAM user/bucket policy (s3:PutObject, s3:GetObject, "
            + "s3:DeleteObject, s3:ListBucket for the bucket and key prefix); Azure Entra ID – assign the RBAC role 'Storage Blob Data Contributor' "
            + "on the storage account or container; Azure SAS – the SAS needs the permissions racwdl for the operation.";

        internal const string BucketNotFoundHint =
            "The bucket/container does not exist: create it or check its name (and for S3 the region and service URL).";

        internal const string RegionHint =
            "The bucket is in a different region or needs another endpoint: set the bucket's region and use its regional service URL.";

        internal const string ApiVersionHint =
            "The storage service does not accept the x-ms-version: set AzureApiVersion to a version supported by this service (e.g. 2025-11-05).";

        internal const string TooLargeHint =
            "The request body is too large for a single request: lower MultipartThresholdBytes/PartSizeBytes.";

        internal const string NotFoundHint =
            "The object does not exist (or the bucket/container name is wrong).";

        private static readonly KeyValuePair<string, string>[] CodeTable =
        {
            Entry("AuthenticationFailed", AzureAuthenticationHint),
            Entry("NoAuthenticationInformation", AzureAuthenticationHint),
            Entry("SignatureDoesNotMatch", S3SignatureHint),
            Entry("InvalidAccessKeyId", S3SignatureHint),
            Entry("InvalidToken", S3SignatureHint),
            Entry("ExpiredToken", S3SignatureHint),
            Entry("RequestTimeTooSkewed", ClockSkewHint),
            Entry("AccessDenied", AccessDeniedHint),
            Entry("AuthorizationPermissionMismatch", AccessDeniedHint),
            Entry("AuthorizationFailure", AccessDeniedHint),
            Entry("AuthorizationResourceTypeMismatch", AccessDeniedHint),
            Entry("NoSuchBucket", BucketNotFoundHint),
            Entry("ContainerNotFound", BucketNotFoundHint),
            Entry("AuthorizationHeaderMalformed", RegionHint),
            Entry("PermanentRedirect", RegionHint),
            Entry("RequestBodyTooLarge", TooLargeHint),
            Entry("EntityTooLarge", TooLargeHint),
        };

        /// <summary>Hint for an error code / status, "" when none applies.</summary>
        internal static string GetHint(string? code, int status, string? message)
        {
            string c = (code ?? "").Trim();
            if (string.Equals(c, "InvalidHeaderValue", StringComparison.OrdinalIgnoreCase)
                && (message ?? "").IndexOf("x-ms-version", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ApiVersionHint;
            }

            if (string.Equals(c, "InvalidQueryParameterValue", StringComparison.OrdinalIgnoreCase)
                && (message ?? "").IndexOf("api-version", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ApiVersionHint;
            }

            // Azurite reports a wrong Shared Key signature as AuthorizationFailure with an authentication message.
            if ((message ?? "").IndexOf("failed to authenticate", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AzureAuthenticationHint;
            }

            foreach (KeyValuePair<string, string> entry in CodeTable)
            {
                if (string.Equals(entry.Key, c, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            switch (status)
            {
                case 401:
                    return AzureOrS3AuthHint(c);
                case 403:
                    // HEAD responses carry no error code: a 403 may be a wrong key/signature as well as a missing permission.
                    return c.Length == 0 ? S3SignatureHint + " " + AzureAuthenticationHint + " Otherwise: " + AccessDeniedHint : AccessDeniedHint;
                case 404:
                    return NotFoundHint;
                case 301:
                case 307:
                    return RegionHint;
                case 413:
                    return TooLargeHint;
                default:
                    return "";
            }
        }

        private static string AzureOrS3AuthHint(string code)
        {
            return code.Length == 0 ? S3SignatureHint + " " + AzureAuthenticationHint : AzureAuthenticationHint;
        }

        private static KeyValuePair<string, string> Entry(string code, string hint)
        {
            return new KeyValuePair<string, string>(code, hint);
        }
    }
}
