# TheLastBlade

A forged katana rotates through moving gold and blue light, framed by obsidian fragments and falling sparks.

An eight-second silent alternative for the UNSEEN first-load intro. 192 frames at 24 fps, 1280 × 720 H.264. The original game intro is still selected; these are review candidates.

- TheLastBlade.mp4 — full animation
- TheLastBlade.blend — editable geometry, lighting, camera and keyframes
- preview.png — title frame (frame 132)

Regenerate from the repository root:

~~~powershell
& 'C:/Program Files/Blender Foundation/Blender 5.2/blender.exe' -b --python Tools/build_intro_alternatives.py -- --variant blade
~~~

Add --preview to render the title frame instead.

Open ../alternatives.html to compare all three directions.

Verified in Unity 6000.6.0f1: video decodes at 1280 × 720, 192 frames, eight seconds; natural completion and cleanup pass. unity-playback.png is a frame captured from the actual Unity decoder.
