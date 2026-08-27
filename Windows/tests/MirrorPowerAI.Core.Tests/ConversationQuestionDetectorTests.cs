using MirrorPowerAI.Core.Sessions;

namespace MirrorPowerAI.Core.Tests;

public sealed class ConversationQuestionDetectorTests
{
    [Theory]
    [InlineData("¿Qué hace el sistema?")]
    [InlineData("que hace el sistema")]
    [InlineData("Me puedes explicar el flujo")]
    [InlineData("Can you explain the flow")]
    [InlineData("La respuesta es: ¿cómo se configura?")]
    [InlineData("Bueno, cuál es el límite de usuarios")]
    [InlineData("Entonces, sería posible desplegarlo hoy")]
    public void IsLikelyQuestion_RecognizesPunctuationAndSpeechCues(string transcript) =>
        Assert.True(ConversationQuestionDetector.IsLikelyQuestion(transcript));

    [Theory]
    [InlineData("Dijo que mañana revisamos el diseño")]
    [InlineData("La reunión empieza cuando llegue el equipo")]
    [InlineData("El sistema captura audio de salida")]
    public void IsLikelyQuestion_DoesNotTriggerOnOrdinaryStatements(string transcript) =>
        Assert.False(ConversationQuestionDetector.IsLikelyQuestion(transcript));

    [Theory]
    [InlineData("¿Qué", true)]
    [InlineData("qué hace el sistema", true)]
    [InlineData("la conversación sigue", true)]
    [InlineData("¿Qué hace?", true)]
    [InlineData("la conversación sigue.", true)]
    [InlineData("¿Qué hace el sistema?", true)]
    public void IsLikelyIncomplete_HoldsEveryNonEmptyForcedChunkRegardlessOfPunctuation(
        string transcript,
        bool expected) =>
        Assert.Equal(expected, ConversationQuestionDetector.IsLikelyIncomplete(transcript, forcedBoundary: true));

    [Fact]
    public void IsLikelyIncomplete_NaturalTurnIsNeverHeld() =>
        Assert.False(ConversationQuestionDetector.IsLikelyIncomplete("¿Qué", forcedBoundary: false));
}
