namespace TerraClaw.AI;

/// <summary>
/// Optional interface for agents that can receive player text from the /agent chat command.
/// </summary>
public interface IPlayerInstructionReceiver
{
    /// <summary>Receives one player instruction on the main game thread.</summary>
    void ReceivePlayerInstruction(string playerName, string instruction);
}
