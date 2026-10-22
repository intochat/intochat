namespace IntoChat.OS.Sdk.Http.Compute;

// Reads trusted ambient context at dispatch time. The returned ID is correlation only;
// ICostMeter independently captures the caller, brain, payer, and trace authority.
public interface IExternalCallContextAccessor
{
    string RequireTraceId();
}

// Custom primary transports must issue at most one provider attempt per SendAsync.
// Put retries and bounded redirects in outer handlers so each dispatch is accounted for.
public interface ISingleAttemptHttpTransport;
