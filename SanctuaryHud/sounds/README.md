# Alert sounds

Voice packs live in subfolders here (`caretaker\`, `announcer\`, …), one WAV
per alert, and the `Alerts · VoicePack` setting picks one by folder name. A
WAV placed directly in this folder is the fallback for any alert the chosen
pack lacks; with no file at all the HUD plays its synthesised tone. The build
deploys this folder to `SanctuaryMods\SanctuaryHud\sounds\` and the release
zips include it. Names, one per alert:

| File | Plays when |
| --- | --- |
| `commander-under-attack.wav` | the commander loses health (at most every 8 s while it continues) |
| `commander-critical.wav` | the commander first drops below the critical fraction |
| `structure-complete.wav` | a structure finishes, per the `CompleteToasts` switches |
| `structure-upgraded.wav` | the same, when the finished structure was an upgrade (falls back to `structure-complete.wav`) |

Any PCM (8/16/24/32-bit) or 32-bit float WAV works, at any sample rate and
channel count. Keep them short (under two seconds) and normalised; the
`Alerts · Volume` setting scales them. A missing file falls back to the tone,
an unreadable one logs a warning and falls back too. Files are read once per
plugin load, so a hot reload (or a game restart) picks up a replaced file.
Sounds are off by default (`Alerts · Sound`).

From an MP3 (ElevenLabs only exports MP3 on the lower tiers), trimmed to the
voice and made mono:

```bash
ffmpeg -i line.mp3 -ac 1 -ar 44100 -af "silenceremove=start_periods=1:start_threshold=-50dB,areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse,apad=pad_dur=0.05" -c:a pcm_s16le commander-under-attack.wav
```
