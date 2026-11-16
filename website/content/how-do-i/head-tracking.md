---
title: Head tracking and looking around
status: full
order: 10
summary: Do you look around with head tracking, face tracking, Tobii or VR? Then your buttons stay free. If not, JoyPro puts looking around on your stick.
---
In a flight sim you need to look around. Some pilots move their head: with head tracking, face tracking, Tobii or VR.
Others look around with a hat on the stick. JoyPro's setup handles both.

:::layer beginner
## The head tracking question

In a flight sim you look around the cockpit all the time. There are two ways to do it:

- **With your head.** A head tracker (such as TrackIR), a webcam with face tracking, a Tobii eye tracker or VR
  glasses follow your head. You turn your head, and the view turns with it.
- **With a button.** A hat on your stick (a small four-way switch under your thumb) turns the view.

So for the big flight sims the setup asks one question: **Do you use head tracking or VR?**

- **Yes** means "I use head tracking, face tracking, Tobii or VR, or I plan to". JoyPro puts no view buttons on your
  HOTAS. Your head does the looking. Only "recenter the view" gets a button.
- **No** means "I look around with a button". Looking around goes on a hat of your stick or throttle, and it gets a
  good place. Only gun, weapon release, countermeasures, brakes, target and trim get a button before looking around.

Not sure? Pick **No**. You can change your answer later in **Settings**, and JoyPro offers to set up again.

The sims that ask: DCS, IL-2 (Great Battles, Korea, Cliffs of Dover, 1946), Falcon BMS, MSFS, FSX, Prepar3D,
X-Plane, War Thunder, Arma, Star Citizen, Elite Dangerous and VTOL VR. The question is in the setup wizard, in the quick
setup and in Settings.

## Without head tracking

The setup puts looking around on your stick. All four directions go on one hat: forward, back, left and right. A
thumb stick on your throttle can get the look axes. This works in DCS and IL-2.

A small stick with no free hat: looking around goes on a hat behind a shift button. Hold the shift button, then use
the hat.

## In ACE COMBAT 8

The game looks around with the right stick of the pretend controller (virtual Xbox pad). JoyPro puts it on a free
hat of your stick. See [[page:how-do-i/xbox-only-games.html|Play an Xbox-only game with a HOTAS]].
:::

:::layer advanced
## Details

- "Yes" also covers "I plan to get head tracking".
- With "No", looking around comes before gear, flaps, the air brake and the radio.
- The look hat of ACE COMBAT 8 keeps its job in other games. In DCS that hat can still trim.
- In IL-2 the setup puts the snap views (forward, back, left, right) on a hat. The look axes go on the throttle's
  mini stick when there is one.
:::

:::layer technical
## Behind it

The planner option is `SetupOptions.HeadTracking`, on the command line [[cli:setup plan]] `--head-tracking yes|no`.
With yes, the snap views and look axes are left out in the sims that support it and the recenter is weighted up. With
no, the look functions rank right after gun, release, countermeasures, brakes, target and trim. Snap views that find no
unshifted hat go whole on a hat behind a shift button, and move back to an unshifted one when a pass frees it.
:::

## Related

- [[page:how-do-i/where-functions-go.html|Where should functions go?]]
- [[page:gui/setup-wizard.html|The setup wizard]]
