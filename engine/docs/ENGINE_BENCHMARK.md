# Engine Command Benchmark

Generated: 2026-03-15 13:45:56
Configuration: Debug | Platform: x64
Warmup runs: 1 | Measured runs: 3

## Command Runtimes (ms)

| Command | Avg | Min | Max |
|---|---:|---:|---:|
| il2 scan | 25.51 | 23.74 | 29.03 |
| dcs scan | 25.24 | 23 | 29.3 |
| db check | 23.75 | 23.51 | 24.19 |
| il2 import | 23.37 | 23.01 | 23.87 |
| devices inputs | 23.11 | 22.7 | 23.44 |
| summary | 23.1 | 22.89 | 23.31 |
| dcs import | 23.06 | 22.66 | 23.43 |
| il2 export | 23.01 | 22.72 | 23.54 |
| profile validate | 22.85 | 22.53 | 23.43 |
| dcs export | 22.33 | 22.11 | 22.46 |
| db summary | 22.29 | 22.16 | 22.46 |

## Internal Hotspots (ms)

| Metric | Avg | Samples |
|---|---:|---:|
| run.total | 23.39 | 33 |
