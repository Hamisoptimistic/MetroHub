# Third-Party Notices and Information

This project incorporates third-party components and libraries subject to the notices and licenses below.

---

## 1. GNU General Public License v3.0 — Section 7 Linking Exception

Copyright (C) 2026 MetroHub Contributors

As an additional permission under Section 7 of the GNU General Public License, version 3 ("GPLv3"), the licensors of MetroHub grant permission to link this software with the following native BASS audio library binaries:

* `bass.dll`
* `bass_fx.dll`
* `bass_aac.dll`
* `basshls.dll`
* `bassopus.dll`
* `basswebm.dll`

You may convey the resulting executable work under the terms of the GNU GPLv3, provided that you also comply with the non-commercial freeware licensing conditions of the native BASS audio binaries.

In accordance with Section 7 of the GNU GPLv3:
1. The Corresponding Source for an executable combination containing MetroHub and the native BASS binaries shall not include the source code for the native BASS binaries.
2. The native BASS binaries remain governed by their respective proprietary licenses and are not covered by the GNU General Public License.

All other terms and conditions of the GNU General Public License v3.0 continue to apply to MetroHub source code.

---

## 2. Proprietary Native Binaries (`lib/native/win-x64`)

The native binary files located in `lib/native/win-x64/` are proprietary closed-source libraries and are excluded from the GNU General Public License v3.0.

### Included Components and Copyright Notices

| Binary | Copyright | License Terms |
| :--- | :--- | :--- |
| `bass.dll` | © Un4seen Developments Ltd. All rights reserved. | Freeware for non-commercial use |
| `basshls.dll` | © Un4seen Developments Ltd. All rights reserved. | Freeware for non-commercial use |
| `bassopus.dll` | © Un4seen Developments Ltd. All rights reserved. | Freeware for non-commercial use |
| `basswebm.dll` | © Un4seen Developments Ltd. All rights reserved. | Freeware for non-commercial use |
| `bass_aac.dll` | © Mareks / Un4seen Developments Ltd. All rights reserved. | Freeware for non-commercial use |
| `bass_fx.dll` | © Arthur Aminov (JOBnik!). All rights reserved. | Freeware for non-commercial use |

### Distribution and Commercial Terms
* **Non-Commercial Use:** These binaries are provided free of charge for personal and non-commercial software use only.
* **Commercial Distribution:** Any commercial use or commercial redistribution requires a separate commercial license from [Un4seen Developments Ltd.](https://www.un4seen.com/).

---

## 3. Managed C# Audio Wrappers (`ManagedBass`, `ManagedBass.Fx`)

MetroHub uses ManagedBass and ManagedBass.Fx to interoperate with the native BASS libraries.

* **Component:** ManagedBass, ManagedBass.Fx
* **Copyright:** (c) Mathew Sachin
* **License:** MIT License

```
MIT License

Copyright (c) Mathew Sachin

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
