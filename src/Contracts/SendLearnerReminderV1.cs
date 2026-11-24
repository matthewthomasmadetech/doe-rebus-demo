namespace RebusDemo.Contracts;

/// <summary>Asks Develop to send a learning plan reminder as a standalone operation.</summary>
public sealed record SendLearnerReminderV1(Guid ReminderId, string LearnerId);
