#!/usr/bin/env python3
"""
================================================================================
Tokenizer Downloader for Semantic Agent Broker
================================================================================
This script downloads official BPE `tokenizer.json` files directly from
Hugging Face model repositories and validates their compatibility with
Semantic Agent Broker's `Microsoft.ML.Tokenizers` loader (`MlTokenCounter`).

Requirements:
  Python 3.8+ (Zero third-party dependencies required; uses standard library urllib)

Usage:
  # Download all default recommended tokenizers (Google Gemma 4, Alibaba Qwen 3.8):
  python download_tokenizers.py

  # Download to a specific folder:
  python download_tokenizers.py --output-dir "./Tokenizers"

  # Download a custom model repository:
  python download_tokenizers.py --repo "Qwen/Qwen3.8-27B" --name "qwen_tokenizer.json"

  # Download with Hugging Face Token (if required):
  python download_tokenizers.py --hf-token "hf_YourSecretTokenHere"
================================================================================
"""

import argparse
import json
import os
import sys
import urllib.request
import urllib.error

# Default models and their target filenames
DEFAULT_MODELS = [
    {
        "name": "gemma4_tokenizer.json",
        "repo": "google/gemma-4-26B-A4B-it",
        "description": "Google Gemma 4 BPE Tokenizer (Official Google)"
    },
    {
        "name": "qwen_tokenizer.json",
        "repo": "Qwen/Qwen3.8-27B",
        "description": "Alibaba Qwen 3.8 BPE Tokenizer (Official Alibaba Qwen)"
    }
]


def validate_bpe_tokenizer(file_path: str) -> bool:
    """
    Validates that the downloaded JSON file meets the requirements of
    SemanticAgentBroker's MlTokenCounter.LoadHuggingFaceTokenizerJson method:
    - Root object must contain "model"
    - "model.type" must be "BPE"
    - "model" must contain "vocab" and "merges"
    """
    try:
        with open(file_path, "r", encoding="utf-8") as f:
            data = json.load(f)

        if not isinstance(data, dict):
            print(f"  [ERROR] Root of {file_path} is not a JSON object.")
            return False

        if "model" not in data:
            print(f"  [ERROR] 'model' property missing from {file_path}.")
            return False

        model_info = data["model"]
        model_type = model_info.get("type")
        if model_type != "BPE":
            print(f"  [WARNING] Tokenizer model type is '{model_type}'. SemanticAgentBroker expects 'BPE'.")
            return False

        if "vocab" not in model_info:
            print(f"  [ERROR] 'vocab' missing from model in {file_path}.")
            return False

        if "merges" not in model_info:
            print(f"  [ERROR] 'merges' missing from model in {file_path}.")
            return False

        vocab_count = len(model_info["vocab"])
        merges_count = len(model_info["merges"])
        print(f"  [OK] Validated BPE schema: {vocab_count:,} vocab entries, {merges_count:,} merges.")
        return True

    except Exception as ex:
        print(f"  [ERROR] JSON validation failed: {ex}")
        return False


def download_tokenizer(repo_id: str, dest_path: str, hf_token: str = None) -> bool:
    """Downloads tokenizer.json from Hugging Face for a given repository ID."""
    url = f"https://huggingface.co/{repo_id}/resolve/main/tokenizer.json"
    headers = {
        "User-Agent": "SemanticAgentBroker-TokenizerDownloader/1.0"
    }
    if hf_token:
        headers["Authorization"] = f"Bearer {hf_token}"

    print(f"\nDownloading: {repo_id}")
    print(f"  URL: {url}")
    print(f"  Target: {dest_path}")

    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            content = response.read()
            with open(dest_path, "wb") as f:
                f.write(content)
        print(f"  [SUCCESS] Downloaded {len(content):,} bytes.")
        return validate_bpe_tokenizer(dest_path)

    except urllib.error.HTTPError as e:
        if e.code == 401:
            print(f"  [ERROR 401 Unauthorized] This repository is gated. Please pass --hf-token <YOUR_TOKEN>.")
        elif e.code == 404:
            print(f"  [ERROR 404 Not Found] 'tokenizer.json' was not found in repo '{repo_id}'.")
        else:
            print(f"  [ERROR HTTP {e.code}] {e.reason}")
        return False
    except Exception as ex:
        print(f"  [ERROR] Failed to download: {ex}")
        return False


def main():
    parser = argparse.ArgumentParser(description="Download tokenizers for Semantic Agent Broker.")
    parser.add_argument("--output-dir", default=".", help="Directory to save downloaded tokenizer files.")
    parser.add_argument("--repo", default=None, help="Custom Hugging Face repo ID (e.g. 'Qwen/Qwen3.8-27B' or 'google/gemma-4-26B-A4B-it').")
    parser.add_argument("--name", default=None, help="Custom filename for the downloaded tokenizer.")
    parser.add_argument("--hf-token", default=os.getenv("HF_TOKEN"), help="Hugging Face API token for gated models.")
    args = parser.parse_args()

    os.makedirs(args.output_dir, exist_ok=True)

    if args.repo:
        filename = args.name or (args.repo.replace("/", "_") + "_tokenizer.json")
        dest = os.path.join(args.output_dir, filename)
        download_tokenizer(args.repo, dest, args.hf_token)
    else:
        print("=" * 70)
        print("Downloading Default Tokenizers for Semantic Agent Broker")
        print("=" * 70)
        success_count = 0
        for item in DEFAULT_MODELS:
            dest = os.path.join(args.output_dir, item["name"])
            print(f"\n--- {item['description']} ---")
            if download_tokenizer(item["repo"], dest, args.hf_token):
                success_count += 1

        print("\n" + "=" * 70)
        print(f"Done! Successfully downloaded and validated {success_count}/{len(DEFAULT_MODELS)} tokenizers.")
        print(f"Saved in: {os.path.abspath(args.output_dir)}")
        print("=" * 70)


if __name__ == "__main__":
    main()
