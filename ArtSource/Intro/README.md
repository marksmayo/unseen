# UNSEEN first-load film

Eight seconds, 192 frames at 24 fps, 1280 × 720, silent H.264 MP4.

The opening moves through a moonlit courtyard framed by bamboo and torii gates. A forged shuriken rotates into place above the silver title; amber motes drift through the blue haze. The final second fades to black before game startup.

- Editable scene: UnseenIntro.blend (geometry, materials, camera and animation; packed title font).
- Generator: ../../Tools/build_intro.py, authored for Blender 5.2.
- Game asset: ../../Assets/Unseen/Resources/Intro/UnseenIntro.mp4.
- Playback: FirstLoadIntro, invoked by UnseenBootstrap.Awake before Boot.

Regenerate from the repository root:

~~~powershell
& 'C:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background --python Tools/build_intro.py
~~~

Add -- --preview to render the title frame to preview.png instead.

The intro plays once per application session (and once per Editor Play session), not on match restarts. Space, Escape or click skips it; external UI can call Skip(). Dedicated servers, batch runs and -skipintro bypass it. Direct calls to Boot() from existing editor probes remain immediate. Decoder errors and preparation/playback timeouts continue to the game. The temporary camera, video player, render texture and loaded video asset are released after playback.

Validation uses Tools/IntroValidation.cs in an isolated Unity project with the production playback script and MP4. It checks imported dimensions/duration, decoded frames, natural completion, skip, cleanup and dedicated-server bypass. This isolates intro verification from unrelated compilation failures in the main project's test assembly.

Run the isolated playback check with `Tools/verify-intro.ps1`. Logs land in `Logs/IntroValidation.Run.log and Logs/IntroValidation.RunSkip.log`; the decoded frame is in `Temp/IntroValidation/playback.png`.
