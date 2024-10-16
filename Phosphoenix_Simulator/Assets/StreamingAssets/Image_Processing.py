import sys
import time
import cv2
import numpy as np
import socket
import struct
import os

from dynaphos.image_processing import sobel_processor, canny_processor
from dynaphos.simulator import GaussianSimulator
from dynaphos.utils import load_params, load_coordinates_from_yaml, Map
from dynaphos.cortex_models import get_visual_field_coordinates_from_cortex_full

print("Python is running!")

FILTER = 'canny'  # choose canny or sobel
THRESHOLD_HIGH = 200  # the high threshold for the canny edge detection

# UDP settings
CHUNK_SIZE = 1024
FRAME_DELIMITER = b"FRAME_DEL"
IMAGE_SIZE = 256*256

# Set up server socket
IP_IN       = '127.0.0.1'   # Localhost
PORT_IN     = 4903          # Port to listen on
IP_OUT      = 'localhost'
PORT_OUT    = 9003

# Get the shutdown file path from the command-line argument
if len(sys.argv) == 3:
    shutdown_file = sys.argv[1].strip('"')
    python_dir = sys.argv[2].strip('"')
else:
    print("No shutdown file path or python script directory path provided.", flush=True)
    print(len(sys.argv))
    sys.exit(1)

def receive_frame(socket_in):
    buffer = b""
    frame_complete = False
    frame_data = b""

    while not frame_complete:
        chunk, addr = socket_in.recvfrom(CHUNK_SIZE)

        # Check if we received the frame delimiter
        if FRAME_DELIMITER in chunk:
            delimiter_index = chunk.find(FRAME_DELIMITER)
            frame_data += chunk[:delimiter_index]  # Collect data until delimiter
            frame_complete = True
        else:
            frame_data += chunk  # Append chunk to frame data

    if len(frame_data) >= IMAGE_SIZE:
        # We received enough data for a complete image
        frame = np.frombuffer(frame_data[:IMAGE_SIZE], dtype=np.uint8)
        # frame = np.frombuffer(frame_data[:400*484], dtype=np.uint8)
        frame = frame.reshape((256, 256))  # Reshape to 256x256 grayscale image
        return True, frame
    else:
        return False, None


def main(params: dict):
    global data

    # parameters van een configuratiebestand
    params['thresholding']['use_threshold'] = False
    coordinates_cortex = load_coordinates_from_yaml(
        python_dir + '/grid_coords_dipole_valid.yaml', n_coordinates=100)
    coordinates_cortex = Map(*coordinates_cortex)
    coordinates_visual_field = get_visual_field_coordinates_from_cortex_full(
        params['cortex_model'], coordinates_cortex)
    simulator = GaussianSimulator(params, coordinates_visual_field)
    resolution = params['run']['resolution']
    fps = params['run']['fps']

    print("Python is ready to activate sockets")
    # open a UDP socket to receive data from Unity
    socket_in = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    socket_in.bind((IP_IN, PORT_IN))
    print("camera socket (in) working from python side")

    # open out socket to send data back to Unity (UDP)
    socket_out = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print("phosphene socket (out) working from python side")

    # main loop
    '''
    # GEBRUIK DIT OM TE KIJKEN OF HET BEELD GOED BINNENKOMT
    while True:
        alles_gut, frame = receive_frame(socket_in)

        if alles_gut:
            # Display the frame (for debugging)
            cv2.imshow("Received Frame", frame)
        if cv2.waitKey(1) & 0xFF == ord('q'):
            break
        if(os.path.exists(shutdown_file)):
            break
    cv2.destroyAllWindows()
    '''

    while True:
        # check if python has to stop already
        if(os.path.exists(shutdown_file)):
            break

        # read frame
        alles_gut, frame = receive_frame(socket_in)

        '''
        # show received frame for debugging purposes
        cv2.imshow("Received Frame", frame)
        if cv2.waitKey(1) & 0xFF == ord('q'):
            break
        '''

        if alles_gut:            
            # het frame is goed ontvangen!
            frame_blur = cv2.GaussianBlur(frame, (3, 3), 0)

        
            method = FILTER
            if method == 'sobel':
                processed_img = sobel_processor(frame_blur)
            elif method == 'canny':
                processed_img = canny_processor(frame_blur, THRESHOLD_HIGH//2, THRESHOLD_HIGH)
            elif method == 'none':
                processed_img = frame_blur
            else:
                raise ValueError(f"{method} is not a valid filter keyword.")

            # Generate phosphenes
            stim_pattern = simulator.sample_stimulus(processed_img, rescale=True)
            phosphenes = simulator(stim_pattern)
            phosphenes = phosphenes.cpu().numpy() * 255
            phosphenes = np.round(phosphenes).astype('uint8')

            data = phosphenes.tobytes()
            # Send data via UDP
            socket_out.sendto(FRAME_DELIMITER, (IP_OUT, PORT_OUT))
            for i in range(0, len(data), CHUNK_SIZE):
                socket_out.sendto(data[i:i+CHUNK_SIZE], (IP_OUT, PORT_OUT))

    '''

    prev = 0
    while True:
        # receive frame from Unity


        # Unpack frame size
        packed_msg_size = data[:payload_size]
        data = data[payload_size:]
        msg_size = struct.unpack("L", packed_msg_size)[0]

        # Retrieve the actual frame data
        while len(data) < msg_size:
            data += conn_in.recv(4096)

        frame_data = data[:msg_size]
        data = data[msg_size:]

        # Decode the frame (convert byte data to image)
        frame = np.frombuffer(frame_data, dtype=np.uint8)
        frame = cv2.imdecode(frame, cv2.IMREAD_COLOR)

        # Create Canny edge detection mask
        # Code Carli
        frame = cv2.resize(frame, resolution)
        frame_gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        frame_blur = cv2.GaussianBlur(frame_gray, (3, 3), 0)
        
        method = FILTER
        if method == 'sobel':
            processed_img = sobel_processor(frame_blur)
        elif method == 'canny':
            processed_img = canny_processor(frame_blur, THRESHOLD_HIGH//2, THRESHOLD_HIGH)
        elif method == 'none':
            processed_img = frame_blur
        else:
            raise ValueError(f"{method} is not a valid filter keyword.")

        # Generate phosphenes
        stim_pattern = simulator.sample_stimulus(processed_img, rescale=True)
        phosphenes = simulator(stim_pattern)
        phosphenes = phosphenes.cpu().numpy() * 255
        phosphenes = np.round(phosphenes).astype('uint8')

        data = phosphenes.tobytes()
        # Send data via UDP
        # Carli
        udp_socket.sendto(frame_delimiter, (IP_OUT, PORT_OUT))
        for i in range(0, len(data), CHUNK_SIZE):
            udp_socket.sendto(data[i:i+CHUNK_SIZE], (IP_OUT, PORT_OUT))
    '''

    socket_in.close()
    socket_out.close()

    print("Python is klaar")

# Hier nog ff naar kijken of dit idd zelfde kan blijven, is van Carli
if __name__ == '__main__':
    _params = load_params(python_dir + '/params.yaml')
    main(_params)
    sys.exit()




'''
import sys
import time

import cv2
import numpy as np
import socket
import os
import struct

from dynaphos.image_processing import sobel_processor, canny_processor
from dynaphos.simulator import GaussianSimulator
from dynaphos.utils import load_params, load_coordinates_from_yaml, Map
from dynaphos.cortex_models import \
    get_visual_field_coordinates_from_cortex_full

print("Bliep bloep bliep")

FILTER = 'canny'  # choose canny or sobel
THRESHOLD_HIGH = 200  # the high threshold for the canny edge detection

UDP_IP_OUT = "localhost";
UDP_PORT_OUT = 9998;
TCP_IP_IN = "127.0.0.1";
TCP_PORT_IN = 4545;

# Maximum UDP packet size
CHUNK_SIZE_OUT = 1024;
# Special code for at the end of each frame, in binary
frame_delimiter = b"FRAME_DEL";

# buffer for incoming frames from Varjo / Unity
data = b""
payload_size = struct.calcsize("L")  # Expected size for the frame size

# Get the shutdown file path from the command-line argument
if len(sys.argv) == 3:
    shutdown_file = sys.argv[1].strip('"');
    python_dir = sys.argv[2].strip('"');
else:
    print("No shutdown file path or python script directory path provided.", flush=True);
    print(len(sys.argv))
    sys.exit(1);

def receive_frame(conn):
    global data
    packet = ""
    ret = True
    while len(data) < payload_size:
        packet = conn.recv(4096)
        if not packet:
            break
        data += packet
    
    if not packet:
        ret = False

    # Unpack frame size MEENEMEN
    packed_msg_size = data[:payload_size]
    data = data[payload_size:]
    msg_size = struct.unpack("L", packed_msg_size)[0]

    # Retrieve the actual frame data
    while len(data) < msg_size:
        data += conn.recv(4096)


    frame_data = data[:msg_size]
    data = data[msg_size:]


    # Decode the frame (convert byte data to image)
    frame = np.frombuffer(frame_data, dtype=np.uint8)
    frame = cv2.imdecode(frame, cv2.IMREAD_COLOR)

    return ret, frame

def main(params: dict):
    params['thresholding']['use_threshold'] = False
    coordinates_cortex = load_coordinates_from_yaml(
        python_dir + '/grid_coords_dipole_valid.yaml', n_coordinates=100)
    coordinates_cortex = Map(*coordinates_cortex)
    coordinates_visual_field = get_visual_field_coordinates_from_cortex_full(
        params['cortex_model'], coordinates_cortex)
    simulator = GaussianSimulator(params, coordinates_visual_field)
    resolution = params['run']['resolution']
    fps = params['run']['fps']

    # Initialize the UDP socket for sending phosphenes to Unity
    udp_socket_out = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print("Socket (out) is working from the python side...")

    # Initialize TCP socket for receiving images from Varjo cam
    tcp_socket_in = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    tcp_socket_in.bind((TCP_IP_IN, TCP_PORT_IN))
    tcp_socket_in.listen(1)
    print("Socket (in) is working from the python side...")

    prev = 0
    time.sleep(1)
    ret, frame = receive_frame(tcp_socket_in)
    while ret: 
        # check if the shutdown file exists
        if os.path.exists(shutdown_file):
            # if it exists: stop the loop so the program can exit
            print("Python found a shutdown file! Stopping now")
            break;
           
        # Capture the video frame by frame
        ret, frame = receive_frame(tcp_socket_in)

        time_elapsed = time.time() - prev
        if time_elapsed > 1 / fps:
            prev = time.time()

            # Create Canny edge detection mask
            frame = cv2.resize(frame, resolution)
            frame = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
            frame = cv2.GaussianBlur(frame, (3, 3), 0)
            
            method = FILTER
            if method == 'sobel':
                processed_img = sobel_processor(frame)
            elif method == 'canny':
                processed_img = canny_processor(frame, THRESHOLD_HIGH//2, THRESHOLD_HIGH)
            elif method == 'none':
                processed_img = frame
            else:
                raise ValueError(f"{method} is not a valid filter keyword.")

            # Generate phosphenes
            stim_pattern = simulator.sample_stimulus(processed_img, rescale=True)
            phosphenes = simulator(stim_pattern)
            phosphenes = phosphenes.cpu().numpy() * 255
            phosphenes = np.round(phosphenes).astype('uint8');

            data_out = phosphenes.tobytes();

            # first, send the frame delimiter to indicate the start of a frame
            udp_socket_out.sendto(frame_delimiter, (UDP_IP_OUT, UDP_PORT_OUT));
            for i in range(0, len(data_out), CHUNK_SIZE_OUT):
                # this loop will send pieces of the frame into the UDP socket
                udp_socket_out.sendto(data[i:i+CHUNK_SIZE_OUT], (UDP_IP_OUT, UDP_PORT_OUT));

    udp_socket_out.shutdown()
    udp_socket_out.close()

    tcp_socket_in.shutdown()
    tcp_socket_in.close()    

    print("python is klaar")

if __name__ == '__main__':
    _params = load_params(python_dir + '/params.yaml')
    main(_params)
    sys.exit()
'''