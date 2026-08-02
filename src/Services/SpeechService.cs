using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;
using AITranslator.Models;
using System.Security;

namespace AITranslator.Services;

public sealed class SpeechService : IDisposable
{
    private readonly MediaPlayer _player = new();
    private IRandomAccessStream? _activeStream;

    public async Task SpeakAsync(string text, string languageCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var synthesizer = CreateSynthesizer(languageCode);

        cancellationToken.ThrowIfCancellationRequested();
        var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
        cancellationToken.ThrowIfCancellationRequested();

        PlayStream(stream);
    }

    public async Task SpeakPronunciationAsync(PronunciationOption pronunciation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pronunciation.Ipa) || string.IsNullOrWhiteSpace(pronunciation.SpeakText))
        {
            return;
        }

        using var synthesizer = CreateSynthesizer(pronunciation.LanguageCode);
        var language = SecurityElement.Escape(pronunciation.LanguageCode) ?? "en-US";
        var ipa = SecurityElement.Escape(PhoneticService.NormalizeIpa(pronunciation.Ipa)) ?? string.Empty;
        var text = SecurityElement.Escape(pronunciation.SpeakText) ?? string.Empty;
        var ssml = $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{language}\"><phoneme alphabet=\"ipa\" ph=\"{ipa}\">{text}</phoneme></speak>";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = await synthesizer.SynthesizeSsmlToStreamAsync(ssml);
            cancellationToken.ThrowIfCancellationRequested();
            PlayStream(stream);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 部分本机语音不接受完整 IPA 音素集，退回按英文文本朗读。
            await SpeakAsync(pronunciation.SpeakText, pronunciation.LanguageCode, cancellationToken);
        }
    }

    public void PlayAudio(string audioUrl)
    {
        if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        _player.Pause();
        _activeStream?.Dispose();
        _activeStream = null;
        _player.Source = MediaSource.CreateFromUri(uri);
        _player.Play();
    }

    public void Dispose()
    {
        _player.Dispose();
        _activeStream?.Dispose();
    }

    private static SpeechSynthesizer CreateSynthesizer(string languageCode)
    {
        var synthesizer = new SpeechSynthesizer();
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(item =>
            item.Language.StartsWith(languageCode, StringComparison.OrdinalIgnoreCase));
        if (voice is null && languageCode.Contains('-', StringComparison.Ordinal))
        {
            var neutralLanguage = languageCode[..languageCode.IndexOf('-', StringComparison.Ordinal)];
            voice = SpeechSynthesizer.AllVoices.FirstOrDefault(item =>
                item.Language.StartsWith(neutralLanguage, StringComparison.OrdinalIgnoreCase));
        }

        if (voice is not null)
        {
            synthesizer.Voice = voice;
        }

        return synthesizer;
    }

    private void PlayStream(SpeechSynthesisStream stream)
    {
        _player.Pause();
        _activeStream?.Dispose();
        _activeStream = stream;
        _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
        _player.Play();
    }
}
