using EndoraReplay.Core.Input;
using Xunit;

namespace EndoraReplay.Core.Tests;

public class MidiTests
{
    private static MidiMessage Cc(int number, int value) => new(MidiControlKind.ControlChange, 1, number, value);

    [Fact]
    public void Parses_note_on_note_off_cc_and_pitch_bend()
    {
        Assert.Equal(new MidiMessage(MidiControlKind.Note, 1, 60, 100), MidiMessage.Parse(0x90 | 60 << 8 | 100 << 16));
        Assert.Equal(new MidiMessage(MidiControlKind.Note, 2, 60, 0), MidiMessage.Parse(0x81 | 60 << 8 | 40 << 16));
        Assert.Equal(new MidiMessage(MidiControlKind.ControlChange, 16, 7, 127), MidiMessage.Parse(0xBF | 7 << 8 | 127 << 16));
        Assert.Equal(new MidiMessage(MidiControlKind.PitchBend, 1, 0, 16383), MidiMessage.Parse(0xE0 | 127 << 8 | 127 << 16));
        Assert.Null(MidiMessage.Parse(0xF8));
    }

    [Theory]
    [InlineData(new[] { 65, 65, 66 }, MidiJogEncoding.Offset64, 4)]
    [InlineData(new[] { 63, 62, 63 }, MidiJogEncoding.Offset64, -4)]
    [InlineData(new[] { 1, 2, 1 }, MidiJogEncoding.TwosComplement, 4)]
    [InlineData(new[] { 127, 126, 127 }, MidiJogEncoding.TwosComplement, -4)]
    [InlineData(new[] { 30, 31, 32, 33 }, MidiJogEncoding.Absolute, 3)]
    [InlineData(new[] { 126, 127, 0, 1 }, MidiJogEncoding.Absolute, 3)]
    public void Detects_jog_encoding_and_direction(int[] values, MidiJogEncoding expected, int sum)
    {
        Assert.Equal(expected, JogDecoder.Detect(values));
        Assert.Equal(sum, JogDecoder.Sum(expected, values));
    }

    [Fact]
    public void Jog_wheel_right_moves_forward_and_left_moves_back_by_frames_per_step()
    {
        var s = new MidiSettings { Jog = new MidiControl { Kind = MidiControlKind.ControlChange, Number = 10 }, JogFramesPerStep = 5 };
        var m = new MidiMapper(s);
        Assert.Equal(new MidiCommand("Jog", 5), Assert.Single(m.Map(Cc(10, 65))));
        Assert.Equal(new MidiCommand("Jog", -5), Assert.Single(m.Map(Cc(10, 63))));
        Assert.Equal(new MidiCommand("Jog", 10), Assert.Single(m.Map(Cc(10, 66))));
        Assert.Equal(MidiJogEncoding.Offset64, m.JogEncoding);

        s.JogInvert = true;
        Assert.Equal(new MidiCommand("Jog", -5), Assert.Single(new MidiMapper(s).Map(Cc(10, 65))));
    }

    [Fact]
    public void T_bar_speed_runs_between_the_learned_ends()
    {
        var s = new MidiSettings { TBar = new MidiControl { Kind = MidiControlKind.ControlChange, Number = 20 }, TBarZero = 120, TBarFull = 20 };
        var m = new MidiMapper(s);
        Assert.Equal(new MidiCommand("SetSpeed", 0.5), Assert.Single(m.Map(Cc(20, 70))));
        Assert.Empty(m.Map(Cc(20, 70)));
        Assert.Equal(1.0, Assert.Single(m.Map(Cc(20, 5))).Value);
        Assert.Equal(MidiMapper.MinSpeed, Assert.Single(m.Map(Cc(20, 127))).Value);
    }

    [Fact]
    public void Buttons_fire_once_per_press()
    {
        var s = new MidiSettings
        {
            Bindings =
            [
                new MidiBinding { Action = "Replay", Control = new MidiControl { Kind = MidiControlKind.Note, Number = 36 } },
                new MidiBinding { Action = "Live", Control = new MidiControl { Kind = MidiControlKind.ControlChange, Number = 40 } },
            ],
        };
        var m = new MidiMapper(s);
        Assert.Equal("Replay", Assert.Single(m.Map(new MidiMessage(MidiControlKind.Note, 1, 36, 90))).Action);
        Assert.Empty(m.Map(new MidiMessage(MidiControlKind.Note, 1, 36, 0)));
        Assert.Empty(m.Map(new MidiMessage(MidiControlKind.Note, 2, 36, 90)));
        Assert.Single(m.Map(Cc(40, 127)));
        Assert.Empty(m.Map(Cc(40, 0)));
        Assert.Single(m.Map(Cc(40, 127)));
        Assert.Single(m.Map(Cc(40, 127)));
    }
}
