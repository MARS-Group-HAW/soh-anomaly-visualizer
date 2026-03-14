using Godot;

public class VisualAgent
{
    public string Id;
    public bool IsMale;
    public Vector3 CurrentPos;
    public Vector3 TargetPos;
    public float WalkCycle;
    public bool ActiveThisTick;
    public Basis CurrentRot = Basis.Identity;
    
    // New detailed attributes from DB:
    public double Hunger;
    public double Thirst;
    public double Mood;
    public double Budget;
    public double Exhaustion;
    public double Bladder;
}
