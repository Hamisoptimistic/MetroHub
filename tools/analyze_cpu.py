import json
import os
import sys

def parse_trace(filename):
    print(f"Loading {filename}...")
    with open(filename, 'r', encoding='utf-8', errors='ignore') as f:
        content = f.read()
    decoder = json.JSONDecoder()
    data, _ = decoder.raw_decode(content)
    
    frames = data['shared']['frames']
    profiles = data['profiles']
    
    print(f"Loaded {len(frames)} frames across {len(profiles)} threads/profiles.\n")
    
    for idx, p in enumerate(profiles):
        p_name = p.get('name', f'Profile {idx}')
        events = p.get('events', [])
        p_type = p.get('type')
        if p_type != 'evented' or not events:
            continue
            
        self_time = [0.0] * len(frames)
        total_time = [0.0] * len(frames)
        
        stack = []
        last_at = None
        for ev in events:
            at = ev['at']
            frame = ev['frame']
            if stack and last_at is not None:
                delta = at - last_at
                if delta > 0:
                    leaf = stack[-1]
                    name = frames[leaf]['name']
                    # Check if leaf is a sampler tick
                    if name in ('CPU_TIME', 'UNMANAGED_CODE_TIME') and len(stack) > 1:
                        target = stack[-2]
                    else:
                        target = leaf
                    self_time[target] += delta
                    for sf in set(stack):
                        total_time[sf] += delta
            last_at = at
            if ev['type'] == 'O':
                stack.append(frame)
            elif ev['type'] == 'C':
                if stack and stack[-1] == frame:
                    stack.pop()
                elif frame in stack:
                    while stack and stack[-1] != frame:
                        stack.pop()
                    if stack:
                        stack.pop()
                        
        total_ms = sum(self_time)
        if total_ms < 100:
            continue
            
        print("=" * 80)
        print(f"THREAD [{idx}]: {p_name} | Total Duration: {total_ms:.1f} ms")
        print("=" * 80)
        
        # Self time top
        self_sorted = sorted(enumerate(self_time), key=lambda x: x[1], reverse=True)
        print("\n--- TOP 10 EXCLUSIVE (SELF) CPU HOTSPOTS ---")
        for rank, (f_idx, ms) in enumerate(self_sorted[:10], 1):
            pct = (ms / total_ms) * 100
            if pct > 0.05:
                print(f"{rank:2d}. {pct:5.2f}% ({ms:8.1f} ms) : {frames[f_idx]['name']}")
                
        # Total time top (Inclusive)
        total_sorted = sorted(enumerate(total_time), key=lambda x: x[1], reverse=True)
        print("\n--- TOP 15 INCLUSIVE (TOTAL) PATHWAYS ---")
        for rank, (f_idx, ms) in enumerate(total_sorted[:15], 1):
            pct = (ms / total_ms) * 100
            if pct > 0.1:
                print(f"{rank:2d}. {pct:5.2f}% ({ms:8.1f} ms) : {frames[f_idx]['name']}")
                
        # Filtered MetroHub methods
        metro_total = [(f_idx, ms) for f_idx, ms in total_sorted if 'MetroHub' in frames[f_idx]['name'] and 'Process64' not in frames[f_idx]['name']]
        if metro_total:
            print("\n--- TOP METROHUB APP METHODS (INCLUSIVE) ---")
            for rank, (f_idx, ms) in enumerate(metro_total[:10], 1):
                pct = (ms / total_ms) * 100
                print(f"{rank:2d}. {pct:5.2f}% ({ms:8.1f} ms) : {frames[f_idx]['name']}")
        print("\n")

if __name__ == '__main__':
    parse_trace('fresh_trace.speedscope.json')
