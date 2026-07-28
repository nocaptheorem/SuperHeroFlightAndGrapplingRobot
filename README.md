# SuperHeroFlightAndGrapplingRobot

[![Godot Engine](https://img.shields.io/badge/Godot-v4.x--.NET-blue?logo=godotengine&logoColor=white)](https://godotengine.org)
[![.NET](https://img.shields.io/badge/.NET-v8.0-purple?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/Language-C%23_12-green?logo=csharp&logoColor=white)](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![License](https://img.shields.io/badge/License-Apache2-yellow.svg)](LICENSE)

> **A physics-based active ragdoll controller with multi-modal traversal mechanics inside Godot 4.**
> Balances, walks, swings, and flies purely through real-time physical forces, closed-loop feedback controllers, Virtual Model Control (VMC), and torque forces applied directly to 6-DoF rigid bodies.

---

## Quick Start & Installation

### Prerequisites
* **Godot Engine v4.x** (specifically the **.NET edition**)
* **.NET 8.0 SDK** (or higher)

### Build & Run
Clone the repository and compile/run the C# solution:

```bash
# Build C# solution and execute in Godot
dotnet build && godot --headless --build-solutions --verbose Main.tscn
```

---

# Technical Specification & Documentation

## 1. What Is It Simulating?

This script implements a hybrid kinematic and dynamic bio-mechanical system. Rather than relying on kinematic, pre-baked character animations for movement, the system models a biomechanically aware, dynamic virtual humanoid. It functions as:

* **A Self-Balancing Humanoid Biped:** Utilizes Virtual Model Control (VMC) and PD controllers to remain upright and react dynamically to terrain.
* **A Dual-Hook Grappling/Swinging System:** Features dynamic cable wrapping, continuous tension calculations, and slingshot physics.
* **An Aerial "Iron Man" Flight Model:** Driven by distributed directional thrust effectors attached to extremities, custom quaternion attitude stabilization, and aerodynamic drag fields.

---

## 2. Technical Breakdown

### Component 1: Kinematic State Tracking & Subtree Properties

Before applying forces, the control system extracts macro-level physical metrics across the ragdoll's hierarchical structure.

* **Mass & Center of Mass (CoM):** Evaluated recursively across all physical bones to calculate the total mass and weighted global CoM dynamically in real-time.

$$ \mathbf{r}_{\text{CoM}} = \frac{\sum (m_i \cdot \mathbf{r}_i)}{\sum m_i} $$

* **Recursive Subtree Gravity Compensation:** Pre-computed branch masses and localized target CoMs allow the system to accurately calculate counter-torques required to achieve weightlessness (gravity compensation) per limb without explicit rigid-body kinematic pinning.

$$ \boldsymbol{\tau}_{\text{comp}} = (\mathbf{r}_{\text{CoM, subtree}} - \mathbf{r}_{\text{pivot}}) \times (m_{\text{subtree}} \mathbf{g}) $$

---

### Component 2: Core Stabilization & Balance (Virtual Model Control)

The character maintains dynamic equilibrium using a layered hierarchical stabilization pipeline:

* **Virtual Model Control (VMC):** Models virtual mechanical springs and dampers between the character's ground contact points (Center of Pressure) and its global CoM. It calculates the necessary virtual force to maintain height and horizontal stability, then maps this via cross-product lever arms into physical joint torques across the leg chain ($\text{Foot} \to \text{Lower Leg} \to \text{Upper Leg}$).

$$ \mathbf{F}_{\text{virtual}} = K_p (\mathbf{r}_{\text{target}} - \mathbf{r}_{\text{CoM}}) - K_d \mathbf{v}_{\text{CoM}} $$
$$ \boldsymbol{\tau}_{\text{joint}} = \mathbf{r}_{\text{joint} \to \text{CoP}} \times \mathbf{F}_{\text{virtual}} $$

* **Hip Gyro Stabilization & Ankle Strategy:** Applies corrective torques to the core to prevent tipping based on ground-normal angular error, and reads ground normals from `ShapeCast3D` sensors to apply local ankle torques keeping feet flush against inclined surfaces.

---

### Component 3: Pose Matching & Bio-Mechanics

To force the physics skeleton to track targeted animation frames (via an `AnimationShadow` Skeleton3D node), the system employs an active Proportional-Derivative (PD) control mechanism:

* **Quaternion Axis-Angle Torque Drives:** Evaluates rotational errors between target joint poses and current physical poses to apply corrective torques.

$$ \mathbf{q}_{\text{err}} = \mathbf{q}_{\text{current}}^{-1} \otimes \mathbf{q}_{\text{target}} $$
$$ \boldsymbol{\tau}_{\text{cmd}} = (K_p \theta) \mathbf{\hat{u}} - K_d \boldsymbol{\omega}_{\text{rel}} $$

* **Impact Relaxation:** If a joint experiences a sudden angular error exceeding `ImpactRelaxationAngle`, the controller drops stiffness ($K_p$) by 95% and spikes damping ($K_d$) by 5x, allowing the ragdoll to absorb extreme shocks (like falling at terminal velocity) without numerical explosion.

---

### Component 4: Dual Grappling Hooks & Cable Wrapping

The grapple system casts environmental rays to establish constraints, behaving as dynamic spring-dampers rather than static cinematic tethers.

* **Dynamic Wrapping:** Solves line-of-sight occlusion by continuously checking raycasts between hand positions and anchors. If blocked, a new waypoint is inserted into a multi-node path array.
* **Tension & Centrifugal Slingshot:** Models the cable as a unilateral distance constraint. Tension scales proportionally to length displacement. Linear momentum tangent to the cable is extracted and converted into a centrifugal boost, yielding Spider-Man-esque slingshot dynamics.

$$ F_{\text{tension}} = \max(0, -K_s (\|\mathbf{x}\| - L) - K_d (\mathbf{v} \cdot \mathbf{\hat{x}})) $$
$$ \mathbf{F}_{\text{slingshot}} = \mathbf{\hat{v}}_{\text{tangent}} (F_{\text{tension}} \cdot C_{\text{boost}}) $$

---

### Component 5: Flight Mechanics & Aerodynamics

* **Quaternion Attitude Controller:** Computes global corrective torques using quaternion error metrics to seamlessly align the torso with instantaneous movement vectors (hovering vs. cruising banks).
* **Distributed Effector Forces:** Maps calculated total flight thrust onto four primary effectors (Hands and Feet). Includes active bracing torques so limbs physically point opposite the thrust vector.
* **Aerodynamics & Fluid Drag:** Calculates varying fluid drag per bone depending on the limb's longitudinal vs. lateral surface exposure relative to current air velocity.

$$ \mathbf{F}_d = - \frac{1}{2} \rho v^2 C_d A_{\text{effective}} \mathbf{\hat{v}} $$

---

## 3. How to Control It and Push It to Its Limits

### Controls Reference Table

| Category | Input / Key | Action |
| --- | --- | --- |
| **Movement** | `W`, `A`, `S`, `D` | Pitch & Roll Steering / Ground Movement |
| **Flight Throttle** | `Space` / `Shift` | Ascend / Descend |
| **Flight Modes** | `F` | Toggle Flight Mode On/Off |
| | `Alt` | Boost Speed (3x Thrust Output) |
| **Grapple Hook** | `Left Mouse` / `Right Mouse` | Fire Left / Right Grappling Hooks |
| | `E` (Hold) | Reel Grapple Cables In |
| **System** | `ESC` | Release/Capture Mouse Cursor |

---

### Extreme Stress Tests & Edge Cases

#### 1. High-Speed Slingshot Launches
* **Action:** Engage both grappling hooks (`Left Click` + `Right Click`) onto a skyscraper corner while falling. Hold `E` to reel in while executing a sharp lateral arc with `A` or `D`, then release.
* **Result:** Evaluates momentum transfer, centrifugal slingshot multiplier, and the capacity of the PD controller to restabilize the ragdoll's posture mid-air.

#### 2. Terminal Velocity Flight Transition
* **Action:** Climb to max altitude in Flight Mode (`F`), point the camera straight down, hold `Alt` for boosted acceleration, and toggle Flight Mode off (`F`) moments before impact.
* **Result:** Tests the `ImpactRelaxationAngle` logic. The ragdoll will seamlessly break posture to absorb the extreme kinetic energy impact, preventing joint tearing.

#### 3. Joint Solver Saturation
* **Action:** Artificially boost `GrappleStiffness` ($> 100,000$) or `FlightSpeed` ($> 200$) in the inspector.
* **Result:** To prevent the `Skeleton3D` from tearing apart under massive tension, the script enforces a hard `PhysicsServer3D` parameter override, raising `SolverIterations` to `64` and hard-clamping linear velocities at 100 m/s.
