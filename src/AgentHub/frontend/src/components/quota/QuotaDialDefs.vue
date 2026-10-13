<template>
<defs>
  <linearGradient id="cr-case-metal" x1="0" y1="0" x2="1" y2="1">
    <stop offset="0" stop-color="var(--dial-case-metal)"/>
    <stop offset=".19" stop-color="var(--dial-case-metal-1)"/>
    <stop offset=".36" stop-color="var(--dial-case-metal-2)"/>
    <stop offset=".52" stop-color="var(--dial-case-metal-3)"/>
    <stop offset=".71" stop-color="var(--dial-case-metal-4)"/>
    <stop offset=".88" stop-color="var(--dial-case-metal-5)"/>
    <stop offset="1" stop-color="var(--dial-case-metal-6)"/>
  </linearGradient>
  <linearGradient id="cr-edge-light" x1=".12" y1=".05" x2=".86" y2="1">
    <stop offset="0" stop-color="var(--dial-edge-light)"/>
    <stop offset=".28" stop-color="var(--dial-edge-light-1)"/>
    <stop offset=".52" stop-color="var(--dial-edge-light-2)"/>
    <stop offset=".79" stop-color="var(--dial-edge-light-3)"/>
    <stop offset="1" stop-color="var(--dial-edge-light-4)"/>
  </linearGradient>
  <linearGradient id="cr-inner-bevel" x1=".15" y1="0" x2=".8" y2="1">
    <stop offset="0" stop-color="var(--dial-inner-bevel)"/>
    <stop offset=".27" stop-color="var(--dial-inner-bevel-1)"/>
    <stop offset=".71" stop-color="var(--dial-inner-bevel-2)"/>
    <stop offset="1" stop-color="var(--dial-inner-bevel-3)"/>
  </linearGradient>
  <linearGradient id="cr-case-side" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="var(--dial-case-side)"/>
    <stop offset=".52" stop-color="var(--dial-case-side-1)"/>
    <stop offset="1" stop-color="var(--dial-case-side-2)"/>
  </linearGradient>
  <radialGradient id="cr-face" cx=".34" cy=".23" r=".87">
    <stop offset="0" stop-color="var(--dial-face)"/>
    <stop offset=".49" stop-color="var(--dial-face-1)"/>
    <stop offset="1" stop-color="var(--dial-face-2)"/>
  </radialGradient>
  <radialGradient id="cr-glass" cx=".21" cy=".05" r=".93">
    <stop offset="0" stop-color="var(--dial-glass)" stop-opacity=".052"/>
    <stop offset=".55" stop-color="var(--dial-glass)" stop-opacity=".008"/>
    <stop offset="1" stop-color="var(--dial-glass)" stop-opacity="0"/>
  </radialGradient>
  <radialGradient id="cr-recess" cx=".51" cy=".52" r=".5">
    <stop offset=".88" stop-color="var(--dial-recess)" stop-opacity="0"/>
    <stop offset="1" stop-color="var(--dial-recess)" stop-opacity=".34"/>
  </radialGradient>
  <linearGradient id="cr-crown-metal" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="var(--dial-crown-metal)"/>
    <stop offset=".07" stop-color="var(--dial-crown-metal-1)"/>
    <stop offset=".17" stop-color="var(--dial-crown-metal-2)"/>
    <stop offset=".39" stop-color="var(--dial-crown-metal-3)"/>
    <stop offset=".63" stop-color="var(--dial-crown-metal-4)"/>
    <stop offset=".89" stop-color="var(--dial-crown-metal-5)"/>
    <stop offset="1" stop-color="var(--dial-crown-metal-6)"/>
  </linearGradient>
  <linearGradient id="cr-crown-end" x1="0" y1="0" x2="1" y2="0">
    <stop offset="0" stop-color="var(--dial-crown-end)"/>
    <stop offset=".3" stop-color="var(--dial-crown-end-1)"/>
    <stop offset=".56" stop-color="var(--dial-crown-end-2)"/>
    <stop offset="1" stop-color="var(--dial-crown-end-3)"/>
  </linearGradient>
  <linearGradient id="cr-green-tube" x1="0" y1="0" x2=".2" y2="1">
    <stop offset="0" stop-color="var(--dial-green-tube)"/>
    <stop offset=".37" stop-color="var(--dial-green-tube-1)"/>
    <stop offset=".76" stop-color="var(--dial-green-tube-2)"/>
    <stop offset="1" stop-color="var(--dial-green-tube-3)"/>
  </linearGradient>
  <linearGradient id="cr-blue-tube" x1=".1" y1="0" x2=".8" y2="1">
    <stop offset="0" stop-color="var(--dial-blue-tube)"/>
    <stop offset=".45" stop-color="var(--dial-blue-tube-1)"/>
    <stop offset="1" stop-color="var(--dial-blue-tube-2)"/>
  </linearGradient>
  <linearGradient id="cr-slot-edge" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="var(--dial-slot-edge)"/>
    <stop offset="1" stop-color="var(--dial-slot-edge-1)" stop-opacity=".5"/>
  </linearGradient>
  <linearGradient id="cr-slot-face" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="var(--dial-slot-face)"/>
    <stop offset="1" stop-color="var(--dial-slot-face-1)"/>
  </linearGradient>
  <linearGradient id="cr-divider" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="var(--dial-divider)" stop-opacity=".15"/>
    <stop offset=".18" stop-color="var(--dial-divider-1)" stop-opacity=".51"/>
    <stop offset=".84" stop-color="var(--dial-divider-2)" stop-opacity=".42"/>
    <stop offset="1" stop-color="var(--dial-divider-2)" stop-opacity="0"/>
  </linearGradient>
  <filter id="cr-grain" x="0" y="0" width="100%" height="100%">
    <feTurbulence type="fractalNoise" baseFrequency=".87" numOctaves="3" stitchTiles="stitch" seed="9"/>
    <feColorMatrix type="saturate" values="0"/>
  </filter>
  <filter id="cr-glow" x="-15%" y="-15%" width="130%" height="130%" color-interpolation-filters="sRGB">
    <feGaussianBlur stdDeviation="2.4"/>
  </filter>
  <filter id="cr-text-shadow" x="-20%" y="-25%" width="140%" height="160%">
    <feDropShadow dx="0" dy="1" stdDeviation=".7" flood-color="var(--dial-recess)" flood-opacity=".65"/>
  </filter>
  <clipPath id="cr-face-clip"><circle cx="205" cy="211" r="158"/></clipPath>
  <path id="cr-left-path" d="M 159.85 86.96 A 132 132 0 0 0 139 325.32"/>
  <path id="cr-right-path" d="M 250.15 86.96 A 132 132 0 0 1 271 325.32"/>
</defs>
</template>
