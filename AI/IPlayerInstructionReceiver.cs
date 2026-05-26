namespace TerraClaw.AI;

public interface IPlayerInstructionReceiver
{
    void ReceivePlayerInstruction(string playerName, string instruction);
}
