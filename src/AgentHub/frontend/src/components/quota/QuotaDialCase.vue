<script setup lang="ts">
import { quotaDialToothFill } from '../../tokens'

const point = (r: number, a: number) => [205 + r * Math.sin(a), 211 - r * Math.cos(a)]
const teeth = Array.from({ length: 164 }, (_, i) => {
  const da = Math.PI * 2 / 164
  const a = i * da
  const points = [point(169.8, a - da * .45), point(172.4, a - da * .19), point(172.8, a + da * .13), point(170.1, a + da * .44)]
  const light = (Math.cos(a + .7) + 1) / 2
  return {
    path: 'M' + points.map(p => p.map(n => n.toFixed(2)).join(',')).join(' L') + ' Z',
    light: `M ${points[0].join(' ')} L ${points[1].join(' ')} L ${points[2].join(' ')}`,
    fill: quotaDialToothFill(light),
    opacity: .08 + light * .48,
  }
})
const grooves = Array.from({ length: 13 }, (_, i) => 189 + i * 4.1)
const vents = Array.from({ length: 8 }, (_, i) => ({ x: 193 + i % 4 * 8, y: 64 + Math.floor(i / 4) * 8 }))
</script>

<template>
  <g aria-hidden="true">
          <g class="cr-crown-art" aria-hidden="true">
            <rect x="366" y="194" width="22" height="42" rx="6" fill="var(--dial-hardware)"/>
            <rect x="372" y="197" width="13" height="35" rx="3" fill="url(#cr-crown-metal)"/>
            <rect x="379" y="184" width="25" height="63" rx="6" fill="var(--dial-hardware-1)" stroke="var(--dial-hardware-2)" stroke-width="1"/>
            <rect x="382" y="185.5" width="20" height="59" rx="4" fill="url(#cr-crown-metal)"/>
            <g>
              <g v-for="y in grooves" :key="y">
                <path :d="`M 383 ${y} H 400`" stroke="var(--dial-groove-dark)" stroke-width="1.35" opacity=".65" />
                <path :d="`M 383 ${y + 1.2} H 400`" stroke="var(--dial-groove-light)" stroke-width=".65" opacity=".5" />
              </g>
            </g>
            <path d="M 383 191 L 383 237" stroke="var(--dial-hardware-3)" stroke-opacity=".55" stroke-width="1"/>
            <rect x="400" y="187" width="6" height="56" rx="2.8" fill="url(#cr-crown-end)"/>
            <path d="M 404.3 194 V 236" stroke="var(--dial-hardware-4)" stroke-opacity=".3" stroke-width=".7"/>
          </g>
          <circle cx="205" cy="215" r="173" fill="url(#cr-case-side)"/>
          <g aria-hidden="true">
            <g v-for="tooth in teeth" :key="tooth.path">
              <path :d="tooth.path" :fill="tooth.fill" stroke="var(--dial-tooth-shadow)" stroke-width=".45" />
              <path :d="tooth.light" fill="none" stroke="var(--dial-tooth-light)" stroke-width=".6" :opacity="tooth.opacity" />
            </g>
          </g>
          <circle cx="205" cy="211" r="170.3" fill="url(#cr-case-metal)" stroke="var(--dial-hardware-5)" stroke-width=".8"/>
          <circle cx="205" cy="211" r="168.7" fill="none" stroke="url(#cr-edge-light)" stroke-width="1.2"/>
          <circle cx="205" cy="211" r="167.25" fill="none" stroke="var(--dial-hardware-6)" stroke-width=".65" opacity=".8"/>
          <circle cx="205" cy="211" r="165.6" fill="url(#cr-inner-bevel)" stroke="url(#cr-edge-light)" stroke-width="1.5"/>
          <circle cx="205" cy="211" r="162.15" fill="var(--dial-hardware-7)" stroke="var(--dial-hardware-8)" stroke-width="1.3"/>
          <circle cx="205" cy="211" r="160.1" fill="none" stroke="var(--dial-hardware-9)" stroke-opacity=".42" stroke-width=".85"/>
          <circle cx="205" cy="211" r="158.4" fill="url(#cr-face)"/>
          <g clip-path="url(#cr-face-clip)">
            <rect x="43" y="49" width="324" height="324" filter="url(#cr-grain)" opacity=".026"/>
            <circle cx="205" cy="211" r="158" fill="url(#cr-glass)"/>
            <circle cx="205" cy="211" r="158" fill="url(#cr-recess)"/>
          </g>
          <circle cx="205" cy="211" r="156.4" fill="none" stroke="var(--dial-hardware-10)" stroke-opacity=".065" stroke-width=".8"/>
          <path d="M 64 117 A 170 170 0 0 1 244 46" fill="none" stroke="var(--dial-hardware-11)" stroke-width="1.05" stroke-opacity=".5"/>

          <g aria-hidden="true">
            <rect x="182" y="55" width="47" height="29" rx="6" fill="url(#cr-slot-edge)"/>
            <rect x="184" y="55" width="43" height="26" rx="5" fill="url(#cr-slot-face)"/>
            <path d="M 188 56 H 223" stroke="var(--dial-hardware-12)" stroke-opacity=".26" stroke-width=".7"/>
            <g>
              <circle v-for="vent in vents" :key="vent.x + ':' + vent.y" :cx="vent.x" :cy="vent.y" r="1.7" fill="var(--dial-vent)" stroke="var(--dial-vent-rim)" stroke-opacity=".22" stroke-width=".6" />
            </g>
          </g>

  </g>
</template>
