using System;
using TerraClaw.Models;

namespace TerraClaw.Events;

public sealed class LlmAgentRegisteredEventArgs : EventArgs
{
    public LlmAgentRegisteredEventArgs(LlmAgentInfo agent)
    {
        Agent = agent;
    }

    public LlmAgentInfo Agent { get; }
}
