using System;

namespace ClaudeRevit.Tools;

// A tool input the model got wrong: missing, null, or the wrong type.
//
// Distinct from any other failure because it is the model's to fix, and because the message is
// written for the model to read: it names the parameter and says what arrived. The dispatcher
// returns it as a plain tool error without a stack trace or the "unexpected error" framing, which
// would otherwise invite a retry of the identical call.
public sealed class ToolInputException : Exception
{
    public ToolInputException(string message) : base(message) { }
}
