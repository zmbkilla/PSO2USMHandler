<h1>PSO2 USM Library</h1>

A c# dll library to handle PSO2 and NGS USM video files.
Uses FFMPEG.Autogen to handle decoding and GI-Cutscenes(https://github.com/ToaHartor/GI-cutscenes) for USM demuxing.
Thanks as well to the pso2 modding community for information regarding usm files


<h2>Setup</h2>

Download the source and place the ffmpeg libraries in a folder called "FFMPEG".
Make sure they match the architecture of your target platform.

Current version used is 9.0.1
- [Auto-Build 2026-08-20 13:45](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-08-20-13-45)

<h2>Usage</h2>

Added a test WPF app to decode and display video out using an image.
<img width="778" height="443" alt="image" src="https://github.com/user-attachments/assets/23dca2db-863e-4463-ae7b-9d72bd7eca42" />

