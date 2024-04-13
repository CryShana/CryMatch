namespace CryMatch.Core.Enums;

public enum WorkMode
{
    /// <summary>
    /// App includes all services required for standalone function (both Matchmaker and Director)
    /// <para>Usage of Redis is optional</para>
    /// </summary>
    Standalone,
    /// <summary>
    /// App includes only the Matchmaker service. 
    /// Use this for horizontal scaling.
    /// <para>Usage of Redis is required</para>
    /// </summary>
    Matchmaker,
    /// <summary>
    /// App includes only the Director service. 
    /// Use this for horizontal scaling.
    /// <para>Usage of Redis is required</para>
    /// </summary>
    Director
};