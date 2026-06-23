---
layout: guide.njk
title: Advanced LEDs with ATSR
description: Build telemetry-driven LED effects in the ATSR-EVO plugin from a ready-made MOZA profile, then import them into your wheel's Individual LEDs.
tags: guide
order: 8
---

AZOM exposes your wheel's LEDs to SimHub's full effects pipeline, but the richest,
telemetry- and input-driven animations come from the
[ATSR-EVO](https://github.com/ATSR-Alex/ATSR-Hub-EVO/) plugin. The flow is: build the
effects against a generic wheel in ATSR, **export** an LED profile, then **import** it into
your MOZA wheel's *Individual LEDs* under AZOM. To save you laying out every LED by hand,
ready-made ATSR profiles are available for each MOZA wheel.

> **Two plugins, one wheel.** ATSR designs the effects; AZOM puts them on the hardware.
> You need both installed in SimHub. AZOM should already have your wheel
> [added as a device](/guides/add-your-device/) — install ATSR-EVO from its
> [GitHub page](https://github.com/ATSR-Alex/ATSR-Hub-EVO/).

## 1 · Add a generic wheel in ATSR

Open **ATSR-Hub EVO** in SimHub's left navigation and go to the **Device Trait** tab. Under
**All Series + Profiles**, click **Universal Wheel Profile** to start a new generic wheel.

![The ATSR-Hub EVO Device Trait tab with the Universal Wheel Profile selected](/docs/ATSR/Setup1.png)

## 2 · Import the MOZA profile

The **Wheel Setup** dialog opens. Click **Import Wheel** and choose the downloaded
`.atsrdevice` file for your wheel — this pre-loads the correct RPM, button and knob layout
so the LEDs map to the right positions. Then pick the **Device Illustration** (**Formula**
or **GT3 Style**) that best matches your rim and click **Next Page →**.

![The Wheel Setup dialog showing Import Wheel and the Formula / GT3 illustration choice](/docs/ATSR/Setup2.png)

Download the profile that matches your wheel:

| Wheel | Profile |
|---|---|
| KS Pro | [MOZA-KS-Pro.atsrdevice](/docs/ATSR/MOZA-KS-Pro.atsrdevice) |
| CS Pro | [MOZA-CS-Pro.atsrdevice](/docs/ATSR/MOZA-CS-Pro.atsrdevice) |
| CS V2P | [MOZA-CS-V2P.atsrdevice](/docs/ATSR/MOZA-CS-V2P.atsrdevice) |
| KS | [MOZA-KS.atsrdevice](/docs/ATSR/MOZA-KS.atsrdevice) |
| GS V2 | [MOZA-GS-V2.atsrdevice](/docs/ATSR/MOZA-GS-V2.atsrdevice) |
| ES | [MOZA-ES.atsrdevice](/docs/ATSR/MOZA-ES.atsrdevice) |
| Vision GS | [MOZA-Vision-GS.atsrdevice](/docs/ATSR/MOZA-Vision-GS.atsrdevice) |
| FSR | [MOZA-FSR.atsrdevice](/docs/ATSR/MOZA-FSR.atsrdevice) |

## 3 · Select your wheelbase / hub device

On the **Product/Device Information** page, select your device from the **Detected Devices** dropdown list.

![The Product/Device Information page with Vendor ID 346E and the Product ID field](/docs/ATSR/Setup3.png)

## 4 · Add the wheel

Continue through the remaining wizard pages, if you selected the correct profile you shouldn't need to make additional changes — click finish to add the wheel. It appears in ATSR's
left navigation as its own device, where you can tune presets and per-element effects under **Effect Customization**.

## 5 · Export the LED profile

Open your new ATSR device and, on the **Device Hub** tab, click **Export LED
Profile**. Save the exported LED profile somewhere you can find it — you will need it for the next step.

![The ATSR device Effect Customization tab with the Export LED Profile link](/docs/ATSR/Setup4.png)

## 6 · Import into AZOM's Individual LEDs

In SimHub, go to **Devices**, open your MOZA wheel, and switch to the **LEDs** tab.

Set **Individual leds profiles** to Individual or Combined to add a new profile:

- **Individual profile only** — the profile *replaces* your other LED effects.
- **Combined** — the profile is layered *on top* of your regular effects .
- **Disabled** — turns the individual profile off without removing it.

Under **Individual leds**, click **Import profile** and select the file you just exported.

![The MOZA wheel LEDs tab with the imported ATSR profile under Individual leds](/docs/ATSR/Setup5.png)

That's it — ATSR now drives your wheel's individual LEDs through AZOM. Tweak the effects any
time in ATSR, re-export, and re-import to update.
