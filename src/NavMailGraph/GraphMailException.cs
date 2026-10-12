using System;

namespace NavMailGraph
{
    /// <summary>
    /// Error raised internally for validation, token, transport and Microsoft Graph failures.
    /// <see cref="GraphMailSender.Send"/> and <see cref="GraphMailSender.TestConnection"/> never let it escape;
    /// they return <see cref="Exception.Message"/> as the error text instead. The message is written so that it
    /// is complete and safe to show to an end user (no secrets by construction).
    /// </summary>
    public sealed class GraphMailException : Exception
    {
        /// <summary>Error code used for validation and configuration problems.</summary>
        public const string ValidationCode = "Validation";

        /// <summary>Error code used for network/transport problems (DNS, TLS, timeouts).</summary>
        public const string TransportCode = "Transport";

        /// <summary>Creates an exception with the code "Unknown".</summary>
        /// <param name="message">Complete, user-facing error text.</param>
        public GraphMailException(string message)
            : this(message, "Unknown", 0, "", false, null)
        {
        }

        /// <summary>Creates an exception.</summary>
        /// <param name="message">Complete, user-facing error text.</param>
        /// <param name="errorCode">Graph "error.code", AADSTS code, "Transport" or "Validation".</param>
        /// <param name="statusCode">HTTP status code, 0 if none.</param>
        /// <param name="requestId">Graph request-id (or Entra correlation id), "" if none.</param>
        /// <param name="isTransient">True when a retry of the whole send may succeed (429, 5xx, timeouts).</param>
        public GraphMailException(string message, string errorCode, int statusCode, string requestId, bool isTransient)
            : this(message, errorCode, statusCode, requestId, isTransient, null)
        {
        }

        /// <summary>Creates an exception wrapping an inner exception.</summary>
        /// <param name="message">Complete, user-facing error text.</param>
        /// <param name="errorCode">Graph "error.code", AADSTS code, "Transport" or "Validation".</param>
        /// <param name="statusCode">HTTP status code, 0 if none.</param>
        /// <param name="requestId">Graph request-id (or Entra correlation id), "" if none.</param>
        /// <param name="isTransient">True when a retry of the whole send may succeed (429, 5xx, timeouts).</param>
        /// <param name="innerException">The underlying exception, may be null.</param>
        public GraphMailException(string message, string errorCode, int statusCode, string requestId, bool isTransient, Exception? innerException)
            : base(message ?? "", innerException)
        {
            ErrorCode = errorCode ?? "";
            StatusCode = statusCode;
            RequestId = requestId ?? "";
            IsTransient = isTransient;
        }

        /// <summary>Graph "error.code" (e.g. ErrorAccessDenied), AADSTS code (e.g. AADSTS7000215), "Transport" or "Validation".</summary>
        public string ErrorCode { get; }

        /// <summary>HTTP status code of the failed call, 0 if there was none.</summary>
        public int StatusCode { get; }

        /// <summary>Graph request-id / Entra correlation-id of the failed call, "" if none. Give this to Microsoft support.</summary>
        public string RequestId { get; }

        /// <summary>True when the failure is likely temporary (throttling, service unavailable, timeout).</summary>
        public bool IsTransient { get; }

        /// <summary>
        /// True when the failed call belongs to the draft flow operations that need Mail.ReadWrite (create draft, add
        /// attachment, create upload session). Used to choose the error hint; not part of the C/AL surface.
        /// </summary>
        internal bool RequiresMailReadWrite { get; set; }

        /// <summary>
        /// True when a mail-delivering call (sendMail, send draft) failed without a definite answer (HTTP 504, timeout): the
        /// message may already have been sent. Such errors are not transient and are never retried automatically.
        /// </summary>
        internal bool OutcomeUnknown { get; set; }

        /// <summary>True when the error is a client-side request timeout (no response within TimeoutSeconds).</summary>
        internal bool IsTimeout { get; set; }

        /// <summary>Creates a validation error. <paramref name="fullText"/> is the complete user-facing text.</summary>
        internal static GraphMailException Validation(string fullText)
        {
            return new GraphMailException(fullText, ValidationCode, 0, "", false, null);
        }

        /// <summary>Creates a validation error with an inner exception.</summary>
        internal static GraphMailException Validation(string fullText, Exception inner)
        {
            return new GraphMailException(fullText, ValidationCode, 0, "", false, inner);
        }
    }
}
