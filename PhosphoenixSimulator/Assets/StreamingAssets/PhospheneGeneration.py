import sys
import time
import cv2
import numpy as np
import socket
import struct
import os
import threading
import tkinter as tk
from tkinter import ttk
import importlib
import inspect
import json

from dynaphos.image_processing import sobel_processor, canny_processor
from dynaphos.simulator import GaussianSimulator
from dynaphos.utils import load_params, load_coordinates_from_yaml, Map
from dynaphos.cortex_models import get_visual_field_coordinates_from_cortex_full

from base_processing_algorithm import BaseProcessingAlgorithm

MEASURE_TIMES = False # Set to True to measure the times of receiving, processing and sending images
N_TIME_MEASUREMENTS = 1000
elapsed_times_r1 = []
elapsed_times_proc = []
elapsed_times_s2 = []

# Paths for storing the files (hard-coded, please change them to match your desired directory)
file_path_r1 = 'C:/Users/XR Zone/Documents/measurements_r1.json'
file_path_proc = 'C:/Users/XR Zone/Documents/measurements_proc.json'
file_path_s2 = 'C:/Users/XR Zone/Documents/measurements_s2.json'

def write_measurements_to_json(file_path, elapsed_times):
    stringified_times_ms = [str(round(t * 1000)) for t in elapsed_times]
    data = {"measurements": stringified_times_ms}
    with open(file_path, 'w') as json_file:
        json.dump(data, json_file)
    print("File stored at " + file_path + " is complete")

print("Python is running!")

if MEASURE_TIMES:
    print("The timing files will be saved to: " + file_path_r1 + ", " + file_path_proc + " and " + file_path_s2)

# UDP settings
CHUNK_SIZE = 1024
FRAME_DELIMITER = b"FRAME_DEL"
EXIT_CODE = b"EXIT_CODE"

# Set up server socket
IP_IN = 'localhost'
PORT_IN = 4906  # Port to listen on
IP_OUT = 'localhost'
PORT_OUT = 9003

# flag if first image hath been received
first_image_received = False



# Get the shutdown file path from the command-line argument
if len(sys.argv) == 5:
    shutdown_file = sys.argv[1].strip('"')
    python_dir = sys.argv[2].strip('"')

    # resolution of phosphenes that are sent back to Unity
    cropWidthPixels = int(sys.argv[3])
    cropHeightPixels = int(sys.argv[4])

else:
    print("Incorrect program arguments! We need 4 of them.", flush=True)
    sys.exit(1)

# Buffers and control variables
buffers = [np.zeros((cropHeightPixels, cropWidthPixels), dtype=np.uint8) for _ in range(2)]
most_recent_image = 0
is_reading_image_buffer = False
image_being_written = 1

# Function to receive a frame in the background
def background_receive(socket_in):
    global most_recent_image, is_reading_image_buffer, image_being_written, first_image_received, elapsed_times_r1
    frame_data = bytearray()  # Accumulate image data here
    IMAGE_SIZE = cropWidthPixels * cropHeightPixels  # Adjust this to your image size
    
    started_recording_time = False

    while True:
        if os.path.exists(shutdown_file):
            print("Background thread: Shutdown file detected. Stopping.")
            break

        try:
            # Receive data
            chunk, addr = socket_in.recvfrom(CHUNK_SIZE)

            # Check for exit code
            if chunk == EXIT_CODE:
                print("Background thread: exit code detected. Stopping.")
                break

            # Check for the frame delimiter
            if chunk == FRAME_DELIMITER:
                if len(frame_data) >= IMAGE_SIZE:
                    print("Frame Complete")

                    # Convert the frame data into an image buffer
                    frame_array = np.frombuffer(frame_data[:IMAGE_SIZE], dtype=np.uint8)
                    frame_array = frame_array.reshape((cropHeightPixels, cropWidthPixels))

                    # Write the frame to the buffer
                    buffers[image_being_written][:] = frame_array

                    # Switch to the next buffer
                    next_buffer = (image_being_written + 1) % 2
                    if not is_reading_image_buffer:
                        most_recent_image = image_being_written
                        image_being_written = next_buffer

                    # Reset for the next frame
                    frame_data = bytearray()

                    # Signal that the first image has been received
                    if not first_image_received:
                        first_image_received = True
                    
                    if MEASURE_TIMES:
                        end_time = time.time()
                        elapsed_time = end_time - start_time  # Calculate elapsed time in seconds
                        print("timed something")
                        if len(elapsed_times_r1) < N_TIME_MEASUREMENTS:
                            elapsed_times_r1.append(elapsed_time)  # Store the elapsed time
                        elif len(elapsed_times_r1) == N_TIME_MEASUREMENTS:
                            write_measurements_to_json(file_path_r1, elapsed_times_r1)
                        started_recording_time = False  
            else:
                if MEASURE_TIMES and not started_recording_time:
                    start_time = time.time() 
                    started_recording_time = True
                # Accumulate the data from each chunk
                frame_data.extend(chunk)

        except socket.error as e:
            print(f"Socket error: {e}")
            break


# Initialize the GUI application
class FilterApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Algorithm Selection")

        # Dropdown for selecting filters
        self.filter_var = tk.StringVar(self)
        self.filter_dropdown = ttk.Combobox(self, textvariable=self.filter_var)
        self.filter_dropdown.set("Select Algorithm")  # Set default value
        self.filter_dropdown.pack(pady=10)

        # Button to start the video processing
        self.start_button = ttk.Button(self, text="Start Simulation", command=self.start_processing)
        self.start_button.pack(pady=10)

        # Populate the dropdown with available formatters
        self.algorithms = self.get_algorithms()
        self.filter_dropdown['values'] = list(self.algorithms.keys())

    def get_algorithms(self):
        # Dictionary to hold algorithm classes
        algorithms_dict = {}

        # Loop through all the files in the algorithms/ directory.
        for file in [f for f in os.listdir(python_dir + "/processing_algorithms") if f.endswith(".py") and not f.startswith("__")]:
            module_name = file.split(".")[0]
            if module_name:
                try:
                    # Import that module dynamically.
                    module = importlib.import_module(f"processing_algorithms.{module_name}")
                    # Get all the classes present in the module.
                    classes = inspect.getmembers(module, inspect.isclass)

                    for name, algorithm_class in classes:
                        if issubclass(algorithm_class, BaseProcessingAlgorithm) and name != "BaseProcessingAlgorithm":
                            algorithms_dict[name] = algorithm_class
                except ModuleNotFoundError as e:
                    print(f"Error importing {module_name}: {e}")
        return algorithms_dict

    def start_processing(self):
        # Get the selected filter
        algorithm_name = self.filter_var.get()
        if algorithm_name in self.algorithms:
            algorithm_class = self.algorithms[algorithm_name]
            algorithm = algorithm_class()

            _params = load_params(python_dir + '/params.yaml')
            #_in_video = 0  # use 0 for webcam, or string with video path
            main(_params, algorithm, self)  # Pass selected filter
        else:
            print("Please select a valid filter!")


def main(params: dict, algorithm, FilterApp):
    global data, is_reading_image_buffer
    # Load coordinates and set up simulator
    FilterApp.destroy()
    coordinates_cortex = load_coordinates_from_yaml(python_dir + '/grid_coords_dipole_valid.yaml', n_coordinates=1500)
    coordinates_cortex = Map(*coordinates_cortex)
    coordinates_visual_field = get_visual_field_coordinates_from_cortex_full(params['cortex_model'], coordinates_cortex)
    simulator = GaussianSimulator(params, coordinates_visual_field)
    resolution = params['run']['resolution']
    fps = params['run']['fps']

    # Open a UDP socket to receive data from Unity
    socket_in = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    socket_in.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1024 * 1024)  # Extra large buffer
    socket_in.bind((IP_IN, PORT_IN))
    print("Camera socket (in) working from Python side")

    # Open out socket to send data back to Unity (UDP)
    socket_out = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print("Phosphene socket (out) working from Python side")

    # Start background thread to receive image from Unity
    receive_thread = threading.Thread(target=background_receive, args=(socket_in,))
    receive_thread.start()

    while not first_image_received:
        time.sleep(0.1)

    # Main loop
    prev = 0

    while True:
        if os.path.exists(shutdown_file):
            print("Python found a shutdown file! Stopping now.")
            break

        # Fix the framerate
        time_elapsed = time.time() - prev
        if time_elapsed > 1 / fps:
            if MEASURE_TIMES:
                start_time = time.time() 
            prev = time.time()

            # Read the frame from the buffer and apply Gaussian blur
            is_reading_image_buffer = True
            frame = cv2.resize(buffers[most_recent_image], resolution, cv2.INTER_LINEAR)
            is_reading_image_buffer = False
            print("Read a new frame")

            # Process the frame using the selected algorithm
            stim_pattern = algorithm.process(frame, params, simulator)
            #start_time = time.time()

            # Generate phosphenes
            phosphenes = simulator(stim_pattern)
            phosphenes = phosphenes.cpu().numpy() * 255
            phosphenes = np.round(phosphenes).astype('uint8')

            resizedPhosphenes = cv2.resize(phosphenes, (cropWidthPixels, cropHeightPixels), interpolation=cv2.INTER_LINEAR)
            data = resizedPhosphenes.tobytes()

            if MEASURE_TIMES:
                end_time = time.time()
                elapsed_time = end_time - start_time  # Calculate elapsed time in seconds
                if len(elapsed_times_proc) < N_TIME_MEASUREMENTS:
                    elapsed_times_proc.append(elapsed_time)  # Store the elapsed time
                elif len(elapsed_times_proc) == N_TIME_MEASUREMENTS:
                    write_measurements_to_json(file_path_proc, elapsed_times_proc)
                start_time = time.time()

            # Send data via UDP
            socket_out.sendto(FRAME_DELIMITER, (IP_OUT, PORT_OUT))
            for i in range(0, len(data), CHUNK_SIZE):
                socket_out.sendto(data[i:min(i + CHUNK_SIZE, len(data))], (IP_OUT, PORT_OUT))
            if MEASURE_TIMES:
                end_time = time.time()
                elapsed_time = end_time - start_time  # Calculate elapsed time in seconds
                if len(elapsed_times_s2) < N_TIME_MEASUREMENTS:
                    elapsed_times_s2.append(elapsed_time)  # Store the elapsed time
                elif len(elapsed_times_s2) == N_TIME_MEASUREMENTS:
                    write_measurements_to_json(file_path_s2, elapsed_times_s2)
            


    receive_thread.join()
    socket_in.close()
    socket_out.close()

    print("Python is done <3")

if __name__ == '__main__':
    app = FilterApp()
    app.mainloop()
    sys.exit()