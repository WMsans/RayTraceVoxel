# Dev Log Video Script Structure

## Table of Contents

- [Script Format](#script-format)
- [Pacing Guide](#pacing-guide)
- [Storytelling Beats](#storytelling-beats)
- [Translating Diffs to Narration](#translating-diffs-to-narration)
- [Tag-to-Narrative Mapping](#tag-to-narrative-mapping)
- [Example Script Excerpt](#example-script-excerpt)

## Script Format

Output the script as a markdown document with this structure:

```markdown
# [Project Name] Dev Log - [Title]

## Cold Open (30-60 seconds)
[Hook that teases the most dramatic/funny moment]

## Intro (30-60 seconds)
[What the project is, what it does, why it exists]

## Act 1: [Phase Name] (2-4 minutes)
[Early development - foundations, first signs of life]

## Act 2: [Phase Name] (3-5 minutes)
[Core development - biggest challenges, funniest bugs, key breakthroughs]

## Act 3: [Phase Name] (2-4 minutes)
[Polish, shipping, final obstacles]

## Outro (30-60 seconds)
[Reflection, lessons learned, what's next]
```

Each section contains only narration text (what the creator reads aloud). No visual
direction cues.

## Pacing Guide

| Script Segment    | Duration Target | Commits to Cover | Notes                              |
|-------------------|-----------------|-------------------|------------------------------------|
| Cold Open         | 30-60s          | 1-2               | Tease the best moment              |
| Intro             | 30-60s          | 0-1               | Set context                        |
| Act 1             | 2-4 min         | 3-6               | Origin story, first working build  |
| Act 2             | 3-5 min         | 5-10              | Meat of the story                  |
| Act 3             | 2-4 min         | 3-6               | Resolution, shipping               |
| Outro             | 30-60s          | 0                  | Wrap up                            |

Total target: 8-15 minutes of narration for 15-25 featured commits.

**Pacing rules:**
- Never cover more than 3 commits in a row without a joke, reaction, or transition
- Spend 20-40 seconds per commit on average; more for dramatic ones, less for montage
- Group related commits (e.g. "the three days I spent debugging auth") into story arcs

## Storytelling Beats

Organize selected commits into these narrative beats:

1. **The Origin** - First commit, initial idea, "what if I just..."
2. **First Signs of Life** - First time something actually works
3. **The Montage** - Rapid-fire progress, things clicking into place (speed through many commits)
4. **The Wall** - A major blocker, nasty bug, or wrong approach discovered
5. **The Breakthrough** - Solving the wall, the "aha" moment
6. **The Funny Bug** - Visual glitch, absurd edge case, "why does this even work"
7. **The Yak Shave** - Getting sidetracked fixing something tangential
8. **The Ship** - Final push, launch, v1.0
9. **The Reflection** - What was learned, what would be done differently

Not every beat needs to appear. Select beats that match the available commit data.

## Translating Diffs to Narration

When turning a commit diff into narration:

**DO:**
- Explain the *problem* being solved, not the code itself
- Use analogies for technical concepts ("It's like trying to sort a deck of cards while someone keeps adding jokers")
- Quote funny commit messages verbatim
- Describe visual bugs in terms of what the user would see
- Use self-deprecating humor about obvious mistakes
- Create tension before revealing fixes ("So there I am, 2 AM, the entire app just... won't start")

**DON'T:**
- Read code line-by-line
- Use jargon without immediately explaining it
- Assume the viewer knows the tech stack
- Spend time on refactors unless they tell a story
- Explain every single commit — montage the boring progress

**Translating technical tags to audience-friendly language:**

| Technical Reality                    | Narration Approach                                    |
|--------------------------------------|-------------------------------------------------------|
| Off-by-one error                     | "Classic programmer mistake #1"                       |
| Null pointer / undefined             | "Forgot to check if the thing actually exists"        |
| Race condition                       | "Two parts of the code fighting over the same thing"  |
| Memory leak                          | "The app was hoarding memory like a digital dragon"   |
| Regex fix                            | "Wrestled with the dark arts of pattern matching"     |
| CSS/layout bug                       | Describe what it *looked* like ("the button was in another dimension") |
| Performance optimization             | Before/after framing, use numbers if available        |
| Security fix                         | "Turns out anyone could just... [describe exploit]"   |
| Revert                               | "Plot twist: that whole feature? Undone."             |

## Tag-to-Narrative Mapping

The scan script tags commits. Map tags to narrative roles:

| Tag              | Narrative Role           | Priority |
|------------------|--------------------------|----------|
| origin           | Opening of the story     | Must use |
| crash            | Dramatic tension         | High     |
| frustration      | Comedy / relatability    | High     |
| hack             | Comedy / self-deprecation| High     |
| classic_bug      | Educational + funny      | High     |
| nasty_bug        | Dramatic tension         | High     |
| visual_bug       | Very visual, great for video | High  |
| revert           | Plot twist               | Medium   |
| rewrite          | Character arc            | Medium   |
| performance      | Before/after satisfaction| Medium   |
| security         | Stakes raising           | Medium   |
| finally          | Payoff moment            | Medium   |
| launch           | Climax / resolution      | High     |
| release          | Milestone marker         | Medium   |
| excitement       | Energy/pacing boost      | Low      |
| surgical_fix     | Satisfying precision     | Low      |
| large_change     | Montage material         | Low      |
| massive_change   | Rewrite arc              | Medium   |
| major_deletion   | Dramatic moment          | Medium   |
| debug_left_in    | Comedy                   | Low      |
| algorithm        | Educational deep-dive    | Medium   |

## Example Script Excerpt

```markdown
# TaskFlow Dev Log - "400 Commits of Chaos"

## Cold Open

So there's this bug. The entire task list just... vanishes. Every single task,
gone. And the worst part? The code that caused it was ONE character. One.
We'll get there.

## Intro

About six months ago I decided to build TaskFlow — a project management app,
because apparently the world needed another one of those. What started as a
weekend project turned into four hundred commits of questionable decisions,
mass, and one mass delete a very memorable encounter with the CSS box model.

## Act 1: "It Compiles, Ship It"

The first commit is just a README. Not even a good one — it literally says
"todo app maybe?" in the description. Peak software engineering.

Three days later, I've got a basic task list rendering. You click a button,
a task appears. You click it again, it does... nothing. But it APPEARS.
And honestly? That felt incredible.

Then I decided to add drag-and-drop. Now, if you've never implemented
drag-and-drop from scratch, imagine trying to juggle while someone keeps
handing you more balls. That's basically what the event handling looked like.

The commit message for when I finally got it working just says "FINALLY."
All caps. That tells you everything you need to know about that week.

## Act 2: "The Authentication Arc"

...
```
