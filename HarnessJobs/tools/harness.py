"""Unity エディタの HarnessLauncher にジョブを投入して結果を待つ。"""
import json
import os
import time
from datetime import datetime

JOBS_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))


def submit(job: dict, name: str) -> str:
    """requests/ にジョブを置く。書き込み途中を拾われないよう .tmp から rename する。"""
    job_id = f"{datetime.now():%Y%m%d_%H%M%S_%f}"[:-3] + f"_{name}"
    req = os.path.join(JOBS_ROOT, "requests")
    os.makedirs(req, exist_ok=True)
    tmp = os.path.join(req, job_id + ".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(job, f)
    os.replace(tmp, os.path.join(req, job_id + ".json"))
    return job_id


def wait(job_id: str, timeout: float = 1800.0, poll: float = 0.5) -> str:
    """status.json が done になるまで待ち、結果ディレクトリを返す。失敗なら例外。"""
    result_dir = os.path.join(JOBS_ROOT, "results", job_id)
    status_path = os.path.join(result_dir, "status.json")
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with open(status_path, encoding="utf-8") as f:
                status = json.load(f)
        except (FileNotFoundError, json.JSONDecodeError):
            status = None
        if status and status["state"] == "done":
            return result_dir
        if status and status["state"] in ("failed", "aborted"):
            raise RuntimeError(f"job {job_id} {status['state']}: {status['message']}")
        time.sleep(poll)
    raise TimeoutError(f"job {job_id} did not finish in {timeout}s")


def run(job: dict, name: str, timeout: float = 1800.0) -> str:
    return wait(submit(job, name), timeout)


def case_dir(result_dir: str, case_name: str) -> str:
    return os.path.join(result_dir, case_name)


def load_variants(result_dir: str, case_name: str) -> list:
    path = os.path.join(result_dir, case_name, "variants.json")
    if not os.path.exists(path):
        path = os.path.join(result_dir, case_name, "summary.json")
        with open(path, encoding="utf-8") as f:
            return [json.load(f)]
    with open(path, encoding="utf-8") as f:
        return json.load(f)
