
Vornoni continent generation

Basic idea:
• Macro Voronoi Tesselation: The map is divided into a handful of huge "macro" cells.
• The "Roll for Land" Phase: The generator randomly flags a few of these macro cell seed points as "Continent Centers."
• Flood-Filling: From those chosen center nodes, the algorithm recursively crawls outward to neighboring micro-cells using a probability algorithm (like a flood-fill or cellular automata), lowering the likelihood of land assignment with every step away from the center

More detail:

Step 1: Scaffold the Macro-Cells

Partition your planet's layout using a low-density Voronoi mesh.
• Scatter 20 to 50 "Macro" seed points across the map.
• Compute the Voronoi regions. These large cells represent the regional tectonic templates or macro-zones of the planet.

Step 2: Choose Continent Seeds (The "Roll for Land")

• Iterate through your macro-cells and randomly flag a small target amount (e.g., 3 to 5 nodes) as Continent Centers.
• Flag the remaining macro-cells as standard Ocean Cores.

Step 3: Run a Spreading BFS (Breadth-First Search)

To build natural continental shelves that fade gradually into deep ocean basins, expand outward from your centers across the macro-cell graph:
• Assign the selected center macro-cells a Macro_Elevation value of 1.0.
• Use a parallel Breadth-First Search (BFS) or Dijkstra’s algorithm to step through adjacent macro-cells.
• For each step away from a center, decrease the elevation value using a falloff decay multiplier:
• If two expanding continents collide, use the maximum value or blend them to create vast land bridges. Macro-cells that are completely unreached by the decay remain at 0.0.

Step 4: Generate the Micro-Cells

• Scatter 1,000 to 10,000 "Micro" seeds to create a finer detail layer
• For each micro-cell, determine which macro-cell its center point falls into.
• Set each micro-cell's base probability threshold to match its parent macro-cell's Macro_Elevation.

Step 5: Fractal Distortion and Edge Warping

If you directly threshold the micro-cells, your continents will inherit the stiff, geometric shapes of the macro-polygons. You need to break up these borders.
• Sample high-frequency fractal noise (like a layered FBM Simplex or Perlin noise).
• Add this noise directly into the micro-cell's base elevation value:
• The Threshold Gate: Mark any micro-cell with a Final_Elevation greater than your sea-level variable (e.g., 0.5) as Land. The rest are flagged as Ocean.


For geometry that falls in a land cell, subsequent levels of heightmap generation would be biased to be above the assigned sea level, if it is in an ocean cell, below.
This is a bias rather than strictly enforced, so subsequent levels can give more detail to the coastline while preserving continent shape.


the following is conceptual python code illustrating the concept (not to be taken as a literal guide)
python
import numpy as np
from scipy.spatial import Voronoi
import random

def generate_multi_continent_planet(num_macro=30, num_micro=2000, num_continents=4):
    # 1. Generate Macro-Cells
    macro_seeds = np.random.rand(num_macro, 2)
    macro_vor = Voronoi(macro_seeds)
    
    # 2. Pick Continent Center Seeds
    continent_centers = random.sample(range(num_macro), num_continents)
    macro_elevation = {i: 0.0 for i in range(num_macro)}
    
    # 3. BFS Spreading to build the continental slopes
    queue = []
    for center in continent_centers:
        macro_elevation[center] = 1.0
        queue.append(center)
        
    # Build adjacency list from Voronoi ridge points
    adj_list = {i: set() for i in range(num_macro)}
    for point_idx1, point_idx2 in macro_vor.ridge_points:
        adj_list[point_idx1].add(point_idx2)
        adj_list[point_idx2].add(point_idx1)
        
    while queue:
        curr = queue.pop(0)
        for neighbor in adj_list[curr]:
            # Decay elevation step by step
            new_elev = macro_elevation[curr] * random.uniform(0.5, 0.75)
            if new_elev > macro_elevation[neighbor] and new_elev > 0.1:
                macro_elevation[neighbor] = new_elev
                queue.append(neighbor)

    # 4. Map Micro-Cells to Macro Parents
    micro_seeds = np.random.rand(num_micro, 2)
    micro_land_mask = []
    
    for p in micro_seeds:
        # Find closest macro seed to inherit base probability
        distances = np.linalg.norm(macro_seeds - p, axis=1)
        closest_macro_idx = np.argmin(distances)
        base_prob = macro_elevation[closest_macro_idx]
        
        # 5. Inject Fractal Noise to rough up the borders
        # Simulating a basic procedural noise function check here
        noise_val = random.uniform(-0.25, 0.25) 
        final_score = base_prob + noise_val
        
        # Land assignment check
        if final_score > 0.4:
            micro_land_mask.append("LAND")
        else:
            micro_land_mask.append("OCEAN")
            
    return micro_seeds, micro_land_mask