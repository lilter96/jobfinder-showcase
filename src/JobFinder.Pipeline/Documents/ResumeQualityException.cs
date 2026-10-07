namespace JobFinder.Pipeline.Documents;

public sealed class ResumeQualityException(string message) : InvalidOperationException(message);
