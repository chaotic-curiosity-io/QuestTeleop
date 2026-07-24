# Robot asset attribution

- `g1_description/` — Unitree G1 humanoid, 29 DoF + Dex3-1 hands
  (`g1_29dof_with_hand_rev_1_0.urdf`, 43 actuated joints), BSD-3-Clause, from
  Unitree Robotics' [unitree_ros](https://github.com/unitreerobotics/unitree_ros/tree/master/robots/g1_description)
  as redistributed by `example-robot-data` v5.0.0. Staged copy of
  `openvla-unity-sim2real`'s `DevVLA/Assets/Robots/g1_description` — baked
  URDF-Importer mesh prefabs (`xxx.prefab` + `xxx_0.asset`) rather than raw
  `.STL` sources. Used to build the semi-transparent robot ghost
  (Tools ▸ Robot Teleop ▸ Robot Overlay ▸ Build G1 Ghost).

- `gr1t2_description/` — Fourier GR-1 T2 + 6-DoF Fourier hands
  (`gr1t2_fourier_hand_6dof.urdf`, 44 actuated + 10 mimic joints), from
  Fourier Intelligence's [Wiki-GRx-Models](https://github.com/FFTAI/Wiki-GRx-Models)
  (`GRX/GR1/gr1t2/` + `Dexterous_hand/fourier_hand_6dof`); license per that
  upstream repository. Staged copy of `openvla-unity-sim2real`'s
  `DevVLA/Assets/Robots/gr1t2_description` — the URDF-Importer's BAKED mesh
  prefabs (`xxx.prefab` + `xxx_0.asset`, vertices already ROS→Unity converted)
  rather than the raw `.STL`/`.obj` sources, since this project has no STL
  importer and the ghost overlay is physics-free. Used to build the
  semi-transparent robot ghost (Tools ▸ Robot Teleop ▸ Robot Overlay ▸
  Build GR-1 Ghost).

- `h1_description/` — Unitree H1 + Inspire hands (`h1_with_hand.urdf` + visual
  `.dae` meshes), BSD-3-Clause, from Unitree Robotics' `unitree_ros`
  (staged copy of `openvla-unity-sim2real`'s
  `DevVLA/Assets/Robots/h1_description`, visual meshes only — STL collision
  meshes and importer-generated prefabs omitted; the ghost overlay is
  physics-free). Used here to build the semi-transparent robot ghost
  (Tools ▸ Robot Teleop ▸ Robot Overlay ▸ Build H1 Ghost).
