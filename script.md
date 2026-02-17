# RayTraceVoxel Dev Log - "The GPU Hates Me: 400 Commits of Voxel Chaos"

## Cold Open

So, there I was. Six hours into a coding session, staring at a screen filled with what looked like a digital acid trip. I was trying to render a simple sphere using ray tracing and voxels in Unity, and instead, I’d created a portal to another dimension where math doesn't exist. My GPU was screaming, my fans were at take-off speeds, and the only thing I had to show for it was a commit message that just said "render something." 

But fast forward two months, and we're flying through the Sponza palace at sixty frames per second with dynamic lighting and procedural grass. How did we get from "dimension-shattering glitch" to "functional game engine"? Well, it involved about 378 commits, several existential crises, and a very personal vendetta against the laws of perspective.

## Intro

Welcome back to the dev log. Today we’re looking at the history of **RayTraceVoxel**. The goal was simple: build a custom voxel engine in Unity that doesn't use meshes. Everything is raymarched directly on the GPU. No triangles, just pure, unadulterated math. 

I’ve been tracking this project for the last few months, and looking back at the commit history is like reading the diary of a man slowly losing his mind to coordinate spaces. Let's dive in.

## Act 1: "The Cube that Could"

The project started, as all great mistakes do, with an "initial commit." This was basically just a README and a dream. I wanted to see if I could even get a compute shader to talk to Unity's render graph.

Three days later, we hit the first milestone: **"feat: render something."** 
"Something" in this case was a single, flickering cube that only appeared if you looked at it from a very specific angle while holding your breath. But it worked! The GPU was drawing something that wasn't a triangle.

Naturally, the next step was the "Hello World" of 3D: **"fix: render a ball."** 
Now, rendering a ball sounds easy, but in a voxel engine, a ball is just a collection of tiny boxes that *think* they're a ball. This is where I met my first real nemesis: **"fix: cross brick artifact."** 

Imagine looking at a beautiful, smooth sphere, but every time you move the camera, it looks like it’s being sliced by invisible lasers. That’s a "cross brick artifact." It happens when your raytracer gets confused about which voxel it’s currently inside when it hits the edge of a data block. It took me a whole weekend to realize I was off by exactly one pixel in my sampling math. Classic programmer mistake #1.

By the end of the first week, I was feeling cocky. I added **"feat: dynamic lighting and pbr"** and a **"player controller."** I could walk around my single sphere in high definition. I was basically John Carmack.

## Act 2: "The Voxel Identity Crisis"

Then came the middle-development slump. I realized that rendering one sphere is easy, but rendering a whole world is... not. I needed a way to handle huge amounts of data, so I started working on a "generation pipeline."

This is where things got technical. I implemented Level of Detail, or LOD. The idea is simple: things far away should be less detailed so the computer doesn't explode. 
The commit message was: **"fix: lod."**
Then, two hours later: **"fix: revert lod."**

Yeah. Plot twist: that whole feature? Undone. Turns out, my "optimization" was actually three times slower than just rendering everything because I’d created a "race condition"—which is just a fancy way of saying two parts of my code were fighting over the same memory like siblings over the last slice of pizza.

I also had a weird detour into **"fix: remove mesh to voxel."** I spent a week trying to convert 3D models into voxels, realized the math was giving me a migraine, and just... deleted the whole thing. Sometimes, the best way to fix a problem is to pretend it never existed.

But then, the breakthrough. **"feat: generation pipeline setup."** I stopped trying to convert meshes and started *generating* the world procedurally using noise. Suddenly, instead of one sphere, I had infinite rolling hills.

## Act 3: "Sponza and the Sea of Bugs"

With a working pipeline, it was time for the "Big One." I wanted to prove this engine could handle real-world complexity, so I did what every graphics programmer does: I imported the Sponza palace.

Commit **"chore: import sponza"** changed 256 files and added 15,000 lines of code. This was the moment of truth. And honestly? It looked incredible. But it also broke *everything*. 

Suddenly, I was dealing with **"fix: crash"** and **"fix: race condition"** every single day. The engine would run fine for five minutes, and then just... vanish. No error message, no warning, just a one-way trip to the desktop. 

One of these bugs was so nasty it took me four days to find. It was in the **"WorldOctreeNode.cs."** It was a threading issue where the world was trying to load new chunks while the renderer was still trying to draw the old ones. It was like trying to change the tires on a car while it’s doing 80 on the highway.

But we pushed through. We added **"feat: grass data"** and **"feat: tree texture"** to make the world feel alive. We even added **"feat: import better looking texture"** to finally move away from the "everything is a gray box" aesthetic.

## Outro

Looking at the most recent commits—things like **"fix: chunk overlapping"** and **"fix: burst compiled memory trimming"**—you can see how far we've come. The big features are done, and now we're just in the "make it not suck" phase.

Building a voxel engine from scratch is a lot like building a house out of Legos while someone is actively trying to knock it over. It’s frustrating, it’s prone to collapse, and you’ll definitely step on something sharp at 2 AM. 

But seeing the Sponza palace rendered in pure voxels, with light bouncing off the stone walls and grass swaying in the wind? It makes all those "revert" commits worth it. 

Next up: we're looking at dynamic destruction. Because if you build a world out of voxels, the first thing people want to do is blow it up. 

Thanks for watching, and I’ll see you in the next 400 commits.
